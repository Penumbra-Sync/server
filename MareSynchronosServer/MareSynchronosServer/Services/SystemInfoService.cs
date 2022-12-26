using MareSynchronos.API;
using MareSynchronosServer.Hubs;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Services;

public class SystemInfoService : IHostedService, IDisposable
{
    private readonly MareMetrics _mareMetrics;
    private readonly IConfigurationService<ServerConfiguration> _config;
    private readonly IServiceProvider _services;
    private readonly IClientIdentificationService _clientIdentService;
    private readonly ILogger<SystemInfoService> _logger;
    private readonly IHubContext<MareHub, IMareHub> _hubContext;
    private Timer _timer;
    public SystemInfoDto SystemInfoDto { get; private set; } = new();

    public SystemInfoService(MareMetrics mareMetrics, IConfigurationService<ServerConfiguration> configurationService, IServiceProvider services,
        IClientIdentificationService clientIdentService, ILogger<SystemInfoService> logger, IHubContext<MareHub, IMareHub> hubContext)
    {
        _mareMetrics = mareMetrics;
        _config = configurationService;
        _services = services;
        _clientIdentService = clientIdentService;
        _logger = logger;
        _hubContext = hubContext;
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

        if (_config.IsMain)
        {
            var onlineUsers = (int)_clientIdentService.GetOnlineUsers().Result;
            SystemInfoDto = new SystemInfoDto()
            {
                OnlineUsers = onlineUsers,
            };

            _logger.LogInformation("Sending System Info, Online Users: {onlineUsers}", onlineUsers);

            _hubContext.Clients.All.Client_UpdateSystemInfo(SystemInfoDto);

            using var scope = _services.CreateScope();
            using var db = scope.ServiceProvider.GetService<MareDbContext>()!;

            _mareMetrics.SetGaugeTo(MetricsAPI.GaugePairs, db.ClientPairs.AsNoTracking().Count());
            _mareMetrics.SetGaugeTo(MetricsAPI.GaugePairsPaused, db.ClientPairs.AsNoTracking().Count(p => p.IsPaused));
            _mareMetrics.SetGaugeTo(MetricsAPI.GaugeGroups, db.Groups.AsNoTracking().Count());
            _mareMetrics.SetGaugeTo(MetricsAPI.GaugeGroupPairs, db.GroupPairs.AsNoTracking().Count());
            _mareMetrics.SetGaugeTo(MetricsAPI.GaugeGroupPairsPaused, db.GroupPairs.AsNoTracking().Count(p => p.IsPaused));
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