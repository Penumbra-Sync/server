using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosServer.Services;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
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
    private readonly string _shardName;
    private readonly int _maxExistingGroupsByUser;
    private readonly int _maxJoinedGroupsByUser;
    private readonly int _maxGroupUserCount;

    public MareHub(MareMetrics mareMetrics, AuthService.AuthServiceClient authServiceClient, FileService.FileServiceClient fileServiceClient,
        MareDbContext mareDbContext, ILogger<MareHub> logger, SystemInfoService systemInfoService, IConfiguration configuration, IHttpContextAccessor contextAccessor,
        IClientIdentificationService clientIdentService)
    {
        _mareMetrics = mareMetrics;
        _authServiceClient = authServiceClient;
        _fileServiceClient = fileServiceClient;
        _systemInfoService = systemInfoService;
        var config = configuration.GetRequiredSection("MareSynchronos");
        _cdnFullUri = new Uri(config.GetValue<string>("CdnFullUrl"));
        _shardName = config.GetValue("ShardName", "Main");
        _maxExistingGroupsByUser = config.GetValue<int>("MaxExistingGroupsByUser", 3);
        _maxJoinedGroupsByUser = config.GetValue<int>("MaxJoinedGroupsByUser", 6);
        _maxGroupUserCount = config.GetValue<int>("MaxGroupUserCount", 100);
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

        var userId = Context.User!.Claims.SingleOrDefault(c => string.Equals(c.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal))?.Value;

        _logger.LogInformation("Connection from {userId}, CI: {characterIdentification}", userId, characterIdentification);

        await Clients.Caller.SendAsync(Api.OnUpdateSystemInfo, _systemInfoService.SystemInfoDto).ConfigureAwait(false);

        var isBanned = await _dbContext.BannedUsers.AsNoTracking().AnyAsync(u => u.CharacterIdentification == characterIdentification).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(userId) && !isBanned && !string.IsNullOrEmpty(characterIdentification))
        {
            var user = (await _dbContext.Users.SingleAsync(u => u.UID == userId).ConfigureAwait(false));
            var existingIdent = await _clientIdentService.GetCharacterIdentForUid(userId).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(existingIdent) && !string.Equals(characterIdentification, existingIdent, StringComparison.Ordinal))
            {
                return new ConnectionDto()
                {
                    ServerVersion = Api.Version
                };
            }

            user.LastLoggedIn = DateTime.UtcNow;
            await _clientIdentService.MarkUserOnline(user.UID, characterIdentification).ConfigureAwait(false);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);

            return new ConnectionDto
            {
                ServerVersion = Api.Version,
                UID = string.IsNullOrEmpty(user.Alias) ? user.UID : user.Alias,
                IsModerator = user.IsModerator,
                IsAdmin = user.IsAdmin,
                ServerInfo = new ServerInfoDto()
                {
                    MaxGroupsCreatedByUser = _maxExistingGroupsByUser,
                    ShardName = _shardName,
                    MaxGroupsJoinedByUser = _maxJoinedGroupsByUser,
                    MaxGroupUserCount = _maxGroupUserCount
                }
            };
        }

        return new ConnectionDto()
        {
            ServerVersion = Api.Version
        };
    }

    [HubMethodName(Api.InvokeCheckClientHealth)]
    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    public async Task<bool> CheckClientHealth()
    {
        return string.IsNullOrEmpty(await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false));
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

        var userCharaIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(userCharaIdent))
        {
            _mareMetrics.DecGauge(MetricsAPI.GaugeAuthorizedConnections);

            _logger.LogInformation("Disconnect from {id}", AuthenticatedUserId);

            await SendDataToAllPairedUsers(Api.OnUserRemoveOnlinePairedPlayer, userCharaIdent).ConfigureAwait(false);

            _dbContext.RemoveRange(_dbContext.Files.Where(f => !f.Uploaded && f.UploaderUID == AuthenticatedUserId));

            await _clientIdentService.MarkUserOffline(AuthenticatedUserId).ConfigureAwait(false);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }
}
