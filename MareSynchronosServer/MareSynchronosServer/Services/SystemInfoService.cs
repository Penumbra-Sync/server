using System;
using System.Threading;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosServer.Hubs;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Services;

public class SystemInfoService : IHostedService, IDisposable
{
    private readonly MareMetrics _mareMetrics;
    private readonly IClientIdentificationService clientIdentService;
    private readonly ILogger<SystemInfoService> _logger;
    private readonly IHubContext<MareHub> _hubContext;
    private Timer _timer;
    public SystemInfoDto SystemInfoDto { get; private set; } = new();

    public SystemInfoService(MareMetrics mareMetrics, IClientIdentificationService clientIdentService, ILogger<SystemInfoService> logger, IHubContext<MareHub> hubContext)
    {
        _mareMetrics = mareMetrics;
        this.clientIdentService = clientIdentService;
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
        //_logger.LogInformation("ThreadPool: {workerThreads} workers available, {ioThreads} IO workers available", workerThreads, ioThreads);

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
                OnlineUsers = clientIdentService.GetOnlineUsers().Result,
                UploadedFiles = 0
            };

            _hubContext.Clients.All.SendAsync(Api.OnUpdateSystemInfo, SystemInfoDto);
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