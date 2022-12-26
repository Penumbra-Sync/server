using System.Security.Claims;
using MareSynchronos.API;
using MareSynchronosServer.Services;
using MareSynchronosServer.Utils;
using MareSynchronosShared;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Protos;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub : Hub<IMareHub>, IMareHub
{
    private readonly MareMetrics _mareMetrics;
    private readonly FileService.FileServiceClient _fileServiceClient;
    private readonly SystemInfoService _systemInfoService;
    private readonly IHttpContextAccessor _contextAccessor;
    private readonly IClientIdentificationService _clientIdentService;
    private readonly MareHubLogger _logger;
    private readonly MareDbContext _dbContext;
    private readonly Uri _cdnFullUri;
    private readonly string _shardName;
    private readonly int _maxExistingGroupsByUser;
    private readonly int _maxJoinedGroupsByUser;
    private readonly int _maxGroupUserCount;

    public MareHub(MareMetrics mareMetrics, FileService.FileServiceClient fileServiceClient,
        MareDbContext mareDbContext, ILogger<MareHub> logger, SystemInfoService systemInfoService,
        IConfigurationService<ServerConfiguration> configuration, IHttpContextAccessor contextAccessor,
        IClientIdentificationService clientIdentService)
    {
        _mareMetrics = mareMetrics;
        _fileServiceClient = fileServiceClient;
        _systemInfoService = systemInfoService;
        _cdnFullUri = configuration.GetValue<Uri>(nameof(ServerConfiguration.CdnFullUrl));
        _shardName = configuration.GetValue<string>(nameof(ServerConfiguration.ShardName));
        _maxExistingGroupsByUser = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxExistingGroupsByUser), 3);
        _maxJoinedGroupsByUser = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxJoinedGroupsByUser), 6);
        _maxGroupUserCount = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxGroupUserCount), 100);
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
    public Task<bool> CheckClientHealth()
    {
        var needsReconnect = !_clientIdentService.IsOnCurrentServer(AuthenticatedUserId);
        if (needsReconnect)
        {
            _logger.LogCallWarning(MareHubLogger.Args(needsReconnect));
        }
        return Task.FromResult(needsReconnect);
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
