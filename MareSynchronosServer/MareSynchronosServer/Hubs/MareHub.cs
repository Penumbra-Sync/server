using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosServer.Services;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using MareSynchronosShared.Protos;
using MareSynchronosShared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Hubs;

public partial class MareHub : Hub
{
    private readonly MareMetrics _mareMetrics;
    private readonly AuthService.AuthServiceClient _authServiceClient;
    private readonly FileService.FileServiceClient _fileServiceClient;
    private readonly SystemInfoService _systemInfoService;
    private readonly IHttpContextAccessor _contextAccessor;
    private readonly IClientIdentificationService _clientIdentService;
    private readonly ILogger<MareHub> _logger;
    private readonly MareDbContext _dbContext;
    private readonly Uri _cdnFullUri;
    public MareHub(MareMetrics mareMetrics, AuthService.AuthServiceClient authServiceClient, FileService.FileServiceClient fileServiceClient,
        MareDbContext mareDbContext, ILogger<MareHub> logger, SystemInfoService systemInfoService, IConfiguration configuration, IHttpContextAccessor contextAccessor,
        IClientIdentificationService clientIdentService)
    {
        _mareMetrics = mareMetrics;
        _authServiceClient = authServiceClient;
        _fileServiceClient = fileServiceClient;
        _systemInfoService = systemInfoService;
        _cdnFullUri = new Uri(configuration.GetRequiredSection("MareSynchronos").GetValue<string>("CdnFullUrl"));
        _contextAccessor = contextAccessor;
        _clientIdentService = clientIdentService;
        _logger = logger;
        _dbContext = mareDbContext;
    }

    [HubMethodName(Api.InvokeHeartbeat)]
    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    public async Task<ConnectionDto> Heartbeat(string characterIdentification)
    {
        _mareMetrics.IncCounter(MetricsAPI.CounterInitializedConnections);

        var userId = Context.User!.Claims.SingleOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;

        _logger.LogInformation("Connection from {userId}, CI: {characterIdentification}", userId, characterIdentification);

        await Clients.Caller.SendAsync(Api.OnUpdateSystemInfo, _systemInfoService.SystemInfoDto).ConfigureAwait(false);

        var isBanned = await _dbContext.BannedUsers.AsNoTracking().AnyAsync(u => u.CharacterIdentification == characterIdentification).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(userId) && !isBanned && !string.IsNullOrEmpty(characterIdentification))
        {
            var user = (await _dbContext.Users.SingleAsync(u => u.UID == userId).ConfigureAwait(false));
            var existingIdent = _clientIdentService.GetCharacterIdentForUid(userId);
            if (!string.IsNullOrEmpty(existingIdent) && characterIdentification != existingIdent)
            {
                return new ConnectionDto()
                {
                    ServerVersion = Api.Version
                };
            }

            user.LastLoggedIn = DateTime.UtcNow;
            _clientIdentService.MarkUserOnline(user.UID, characterIdentification);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);

            return new ConnectionDto
            {
                ServerVersion = Api.Version,
                UID = string.IsNullOrEmpty(user.Alias) ? user.UID : user.Alias,
                IsModerator = user.IsModerator,
                IsAdmin = user.IsAdmin
            };
        }

        return new ConnectionDto()
        {
            ServerVersion = Api.Version
        };
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Connection from {ip}", _contextAccessor.GetIpAddress());
        _mareMetrics.IncGauge(MetricsAPI.GaugeConnections);
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override async Task OnDisconnectedAsync(Exception exception)
    {
        _mareMetrics.DecGauge(MetricsAPI.GaugeConnections);

        var userCharaIdent = _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId);

        if (!string.IsNullOrEmpty(userCharaIdent))
        {
            var user = await _dbContext.Users.SingleOrDefaultAsync(u => u.UID == AuthenticatedUserId)!.ConfigureAwait(false);
            _mareMetrics.DecGauge(MetricsAPI.GaugeAuthorizedConnections);

            _logger.LogInformation("Disconnect from {id}", AuthenticatedUserId);

            var query =
                from userToOther in _dbContext.ClientPairs
                join otherToUser in _dbContext.ClientPairs
                    on new
                    {
                        user = userToOther.UserUID,
                        other = userToOther.OtherUserUID

                    } equals new
                    {
                        user = otherToUser.OtherUserUID,
                        other = otherToUser.UserUID
                    }
                where
                    userToOther.UserUID == user.UID
                    && !userToOther.IsPaused
                    && !otherToUser.IsPaused
                select otherToUser.UserUID;
            var otherEntries = await query.ToListAsync().ConfigureAwait(false);

            await Clients.Users(otherEntries).SendAsync(Api.OnUserRemoveOnlinePairedPlayer, userCharaIdent).ConfigureAwait(false);

            _dbContext.RemoveRange(_dbContext.Files.Where(f => !f.Uploaded && f.UploaderUID == user.UID));

            _clientIdentService.MarkUserOffline(user.UID);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    protected string AuthenticatedUserId => Context.User?.Claims?.SingleOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value ?? "Unknown";

    protected async Task<User> GetAuthenticatedUserUntrackedAsync()
    {
        return await _dbContext.Users.AsNoTrackingWithIdentityResolution().SingleAsync(u => u.UID == AuthenticatedUserId).ConfigureAwait(false);
    }
}