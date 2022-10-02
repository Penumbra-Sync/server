using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosServer.Hubs;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Services;

public class SystemInfoService : IHostedService, IDisposable
{
    private readonly MareMetrics _mareMetrics;
    private readonly IServiceProvider _services;
    private readonly IClientIdentificationService _clientIdentService;
    private readonly ILogger<SystemInfoService> _logger;
    private readonly IHubContext<MareHub> _hubContext;
    private Timer _timer;
    public SystemInfoDto SystemInfoDto { get; private set; } = new();

    public SystemInfoService(MareMetrics mareMetrics, IServiceProvider services, IClientIdentificationService clientIdentService, ILogger<SystemInfoService> logger, IHubContext<MareHub> hubContext)
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
        if (string.IsNullOrEmpty(secondaryServer) || secondaryServer == "0")
        {
            SystemInfoDto = new SystemInfoDto()
            {
                CacheUsage = 0,
                CpuUsage = 0,
                RAMUsage = 0,
                NetworkIn = 0,
                NetworkOut = 0,
                OnlineUsers = _clientIdentService.GetOnlineUsers().Result,
                UploadedFiles = 0
            };

            _hubContext.Clients.All.SendAsync(Api.OnUpdateSystemInfo, SystemInfoDto);

            using var scope = _services.CreateScope();
            using var db = scope.ServiceProvider.GetService<MareDbContext>()!;

            // todo: add db context and grab pairs
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