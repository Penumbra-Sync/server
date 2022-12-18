using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosServer.Services;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Protos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Hubs;

public partial class MareHub : Hub<IMareHub>, IMareHub
{
    private readonly MareMetrics _mareMetrics;
    private readonly FileService.FileServiceClient _fileServiceClient;
    private readonly SystemInfoService _systemInfoService;
    private readonly IHttpContextAccessor _contextAccessor;
    private readonly GrpcClientIdentificationService _clientIdentService;
    private readonly MareHubLogger _logger;
    private readonly MareDbContext _dbContext;
    private readonly Uri _cdnFullUri;
    private readonly string _shardName;
    private readonly int _maxExistingGroupsByUser;
    private readonly int _maxJoinedGroupsByUser;
    private readonly int _maxGroupUserCount;

    public MareHub(MareMetrics mareMetrics, FileService.FileServiceClient fileServiceClient,
        MareDbContext mareDbContext, ILogger<MareHub> logger, SystemInfoService systemInfoService, IConfiguration configuration, IHttpContextAccessor contextAccessor,
        GrpcClientIdentificationService clientIdentService)
    {
        _mareMetrics = mareMetrics;
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
        _logger = new MareHubLogger(this, logger);
        _dbContext = mareDbContext;
    }

    [Authorize(Policy = "Authenticated")]
    public async Task<ConnectionDto> Heartbeat(string characterIdentification)
    {
        _mareMetrics.IncCounter(MetricsAPI.CounterInitializedConnections);

        var userId = Context.User!.Claims.SingleOrDefault(c => string.Equals(c.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal))?.Value;

        _logger.LogCallInfo(MareHubLogger.Args(characterIdentification));

        await Clients.Caller.Client_UpdateSystemInfo(_systemInfoService.SystemInfoDto).ConfigureAwait(false);

        var isBanned = await _dbContext.BannedUsers.AsNoTracking().AnyAsync(u => u.CharacterIdentification == characterIdentification).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(userId) && !isBanned && !string.IsNullOrEmpty(characterIdentification))
        {
            var user = (await _dbContext.Users.SingleAsync(u => u.UID == userId).ConfigureAwait(false));
            var existingIdent = _clientIdentService.GetCharacterIdentForUid(userId);
            if (!string.IsNullOrEmpty(existingIdent) && !string.Equals(characterIdentification, existingIdent, StringComparison.Ordinal))
            {
                _logger.LogCallWarning(MareHubLogger.Args(characterIdentification, "Failure", "LoggedIn"));

                return new ConnectionDto()
                {
                    ServerVersion = IMareHub.ApiVersion
                };
            }

            user.LastLoggedIn = DateTime.UtcNow;
            _clientIdentService.MarkUserOnline(user.UID, characterIdentification);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);

            _logger.LogCallInfo(MareHubLogger.Args(characterIdentification, "Success"));

            return new ConnectionDto
            {
                ServerVersion = IMareHub.ApiVersion,
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

        _logger.LogCallWarning(MareHubLogger.Args(characterIdentification, "Failure"));

        return new ConnectionDto()
        {
            ServerVersion = IMareHub.ApiVersion
        };
    }

    [Authorize(Policy = "Authenticated")]
    public async Task<bool> CheckClientHealth()
    {
        var needsReconnect = !_clientIdentService.IsOnCurrentServer(AuthenticatedUserId);
        if (needsReconnect)
        {
            _logger.LogCallWarning(MareHubLogger.Args(needsReconnect));
        }
        return needsReconnect;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogCallInfo(MareHubLogger.Args(_contextAccessor.GetIpAddress()));
        _mareMetrics.IncGauge(MetricsAPI.GaugeConnections);
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override async Task OnDisconnectedAsync(Exception exception)
    {
        _mareMetrics.DecGauge(MetricsAPI.GaugeConnections);

        var userCharaIdent = _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId);

        if (!string.IsNullOrEmpty(userCharaIdent))
        {
            _mareMetrics.DecGauge(MetricsAPI.GaugeAuthorizedConnections);

            _logger.LogCallInfo();
            _clientIdentService.MarkUserOffline(AuthenticatedUserId);

            await SendOfflineToAllPairedUsers(userCharaIdent).ConfigureAwait(false);

            _dbContext.RemoveRange(_dbContext.Files.Where(f => !f.Uploaded && f.UploaderUID == AuthenticatedUserId));

            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }
}
