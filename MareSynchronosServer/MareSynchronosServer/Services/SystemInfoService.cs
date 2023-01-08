using MareSynchronos.API;
using MareSynchronosServer.Hubs;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace MareSynchronosServer.Services;

public class SystemInfoService : IHostedService, IDisposable
{
    private readonly MareMetrics _mareMetrics;
    private readonly IConfigurationService<ServerConfiguration> _config;
    private readonly IServiceProvider _services;
    private readonly ILogger<SystemInfoService> _logger;
    private readonly IHubContext<MareHub, IMareHub> _hubContext;
    private readonly IConnectionMultiplexer _redis;
    private Timer _timer;
    public SystemInfoDto SystemInfoDto { get; private set; } = new();

    public SystemInfoService(MareMetrics mareMetrics, IConfigurationService<ServerConfiguration> configurationService, IServiceProvider services,
        ILogger<SystemInfoService> logger, IHubContext<MareHub, IMareHub> hubContext, IConnectionMultiplexer connectionMultiplexer)
    {
        _mareMetrics = mareMetrics;
        _config = configurationService;
        _services = services;
        _logger = logger;
        _hubContext = hubContext;
        _redis = connectionMultiplexer;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("System Info Service started");

        _timer = new Timer(PushSystemInfo, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        return Task.CompletedTask;
    }

    private void PushSystemInfo(object state)
    {
        ThreadPool.GetAvailableThreads(out int workerThreads, out int ioThreads);

        _mareMetrics.SetGaugeTo(MetricsAPI.GaugeAvailableWorkerThreads, workerThreads);
        _mareMetrics.SetGaugeTo(MetricsAPI.GaugeAvailableIOWorkerThreads, ioThreads);

        var endpoint = _redis.GetEndPoints().First();
        var onlineUsers = (_redis.GetServer(endpoint).Keys(pattern: "UID:*").Count());
        SystemInfoDto = new SystemInfoDto()
        {
            OnlineUsers = onlineUsers,
        };

        if (_config.IsMain)
        {
            _logger.LogInformation("Sending System Info, Online Users: {onlineUsers}", onlineUsers);

            _hubContext.Clients.All.Client_UpdateSystemInfo(SystemInfoDto);

            using var scope = _services.CreateScope();
            using var db = scope.ServiceProvider.GetService<MareDbContext>()!;

            _mareMetrics.SetGaugeTo(MetricsAPI.GaugeAuthorizedConnections, onlineUsers);
            _mareMetrics.SetGaugeTo(MetricsAPI.GaugePairs, db.ClientPairs.AsNoTracking().Count());
            _mareMetrics.SetGaugeTo(MetricsAPI.GaugePairsPaused, db.ClientPairs.AsNoTracking().Count(p => p.IsPaused));
            _mareMetrics.SetGaugeTo(MetricsAPI.GaugeGroups, db.Groups.AsNoTracking().Count());
            _mareMetrics.SetGaugeTo(MetricsAPI.GaugeGroupPairs, db.GroupPairs.AsNoTracking().Count());
            _mareMetrics.SetGaugeTo(MetricsAPI.GaugeGroupPairsPaused, db.GroupPairs.AsNoTracking().Count(p => p.IsPaused));
            _mareMetrics.SetGaugeTo(MetricsAPI.GaugeUsersRegistered, db.Users.AsNoTracking().Count());
        }
    }


    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}