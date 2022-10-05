using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosServer.Hubs;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Services;

public class SystemInfoService : IHostedService, IDisposable
{
    private readonly MareMetrics _mareMetrics;
    private readonly IServiceProvider _services;
    private readonly GrpcClientIdentificationService _clientIdentService;
    private readonly ILogger<SystemInfoService> _logger;
    private readonly IHubContext<MareHub> _hubContext;
    private Timer _timer;
    private string _shardName;
    public SystemInfoDto SystemInfoDto { get; private set; } = new();

    public SystemInfoService(MareMetrics mareMetrics, IConfiguration configuration, IServiceProvider services, GrpcClientIdentificationService clientIdentService, ILogger<SystemInfoService> logger, IHubContext<MareHub> hubContext)
    {
        _mareMetrics = mareMetrics;
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

        var secondaryServer = Environment.GetEnvironmentVariable("SECONDARY_SERVER");
        if (string.IsNullOrEmpty(secondaryServer) || string.Equals(secondaryServer, "0", StringComparison.Ordinal))
        {
            SystemInfoDto = new SystemInfoDto()
            {
                OnlineUsers = (int)_clientIdentService.GetOnlineUsers().Result,
            };

            _hubContext.Clients.All.SendAsync(Api.OnUpdateSystemInfo, SystemInfoDto);

            using var scope = _services.CreateScope();
            using var db = scope.ServiceProvider.GetService<MareDbContext>()!;

            _mareMetrics.SetGaugeTo(MetricsAPI.GaugePairs, db.ClientPairs.Count());
            _mareMetrics.SetGaugeTo(MetricsAPI.GaugePairsPaused, db.ClientPairs.Count(p => p.IsPaused));
            _mareMetrics.SetGaugeTo(MetricsAPI.GaugeGroups, db.Groups.Count());
            _mareMetrics.SetGaugeTo(MetricsAPI.GaugeGroupPairs, db.GroupPairs.Count());
            _mareMetrics.SetGaugeTo(MetricsAPI.GaugeGroupPairsPaused, db.GroupPairs.Count(p => p.IsPaused));
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