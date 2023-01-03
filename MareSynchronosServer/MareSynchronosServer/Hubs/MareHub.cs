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

namespace MareSynchronosServer.Hubs;

[Authorize(Policy = "Authenticated")]
public partial class MareHub : Hub<IMareHub>, IMareHub
{
    private readonly MareMetrics _mareMetrics;
    private readonly FileService.FileServiceClient _fileServiceClient;
    private readonly SystemInfoService _systemInfoService;
    private readonly IHttpContextAccessor _contextAccessor;
    private readonly IClientIdentificationService _clientIdentService;
    private readonly MareHubLogger _logger;
    private readonly MareDbContext _dbContext;
    private readonly Uri _mainCdnFullUrl;
    private readonly string _shardName;
    private readonly int _maxExistingGroupsByUser;
    private readonly int _maxJoinedGroupsByUser;
    private readonly int _maxGroupUserCount;
    private readonly IConfigurationService<ServerConfiguration> _configurationService;

    public MareHub(MareMetrics mareMetrics, FileService.FileServiceClient fileServiceClient,
        MareDbContext mareDbContext, ILogger<MareHub> logger, SystemInfoService systemInfoService,
        IConfigurationService<ServerConfiguration> configuration, IHttpContextAccessor contextAccessor,
        IClientIdentificationService clientIdentService)
    {
        _mareMetrics = mareMetrics;
        _fileServiceClient = fileServiceClient;
        _systemInfoService = systemInfoService;
        _configurationService = configuration;
        _mainCdnFullUrl = configuration.GetValue<Uri>(nameof(ServerConfiguration.CdnFullUrl));
        _shardName = configuration.GetValue<string>(nameof(ServerConfiguration.ShardName));
        _maxExistingGroupsByUser = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxExistingGroupsByUser), 3);
        _maxJoinedGroupsByUser = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxJoinedGroupsByUser), 6);
        _maxGroupUserCount = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxGroupUserCount), 100);
        _contextAccessor = contextAccessor;
        _clientIdentService = clientIdentService;
        _logger = new MareHubLogger(this, logger);
        _dbContext = mareDbContext;
    }

    [Authorize(Policy = "Identified")]
    public async Task<ConnectionDto> GetConnectionDto()
    {
        _logger.LogCallInfo();

        _mareMetrics.IncCounter(MetricsAPI.CounterInitializedConnections);

        await Clients.Caller.Client_UpdateSystemInfo(_systemInfoService.SystemInfoDto).ConfigureAwait(false);

        var dbUser = _dbContext.Users.SingleOrDefault(f => f.UID == UserUID);
        dbUser.LastLoggedIn = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        return new ConnectionDto()
        {
            ServerVersion = IMareHub.ApiVersion,
            UID = string.IsNullOrEmpty(dbUser.Alias) ? dbUser.UID : dbUser.Alias,
            IsAdmin = dbUser.IsAdmin,
            IsModerator = dbUser.IsModerator,
            ServerInfo = new ServerInfoDto()
            {
                MaxGroupsCreatedByUser = _maxExistingGroupsByUser,
                ShardName = _shardName,
                MaxGroupsJoinedByUser = _maxJoinedGroupsByUser,
                MaxGroupUserCount = _maxGroupUserCount
            }
        };
    }

    [Authorize(Policy = "Authenticated")]
    public async Task<ConnectionDto> Heartbeat(string characterIdentification)
    {
        return new ConnectionDto()
        {
            ServerVersion = IMareHub.ApiVersion
        };
    }

    [Authorize(Policy = "Authenticated")]
    public Task<bool> CheckClientHealth()
    {
        var needsReconnect = !_clientIdentService.IsOnCurrentServer(UserUID);
        if (needsReconnect)
        {
            _logger.LogCallWarning(MareHubLogger.Args(needsReconnect));
        }
        return Task.FromResult(needsReconnect);
    }

    [Authorize(Policy = "Authenticated")]
    public override async Task OnConnectedAsync()
    {
        _mareMetrics.IncGauge(MetricsAPI.GaugeConnections);

        try
        {
            _logger.LogCallInfo(MareHubLogger.Args(_contextAccessor.GetIpAddress(), UserCharaIdent));

            _clientIdentService.MarkUserOnline(UserUID, UserCharaIdent);
        }
        catch { }

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Authenticated")]
    public override async Task OnDisconnectedAsync(Exception exception)
    {
        _mareMetrics.DecGauge(MetricsAPI.GaugeConnections);

        try
        {
            _logger.LogCallInfo(MareHubLogger.Args(_contextAccessor.GetIpAddress(), UserCharaIdent));

            _clientIdentService.MarkUserOffline(UserUID);

            await SendOfflineToAllPairedUsers(UserCharaIdent).ConfigureAwait(false);

            _dbContext.RemoveRange(_dbContext.Files.Where(f => !f.Uploaded && f.UploaderUID == UserUID));
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        catch { }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }
}
