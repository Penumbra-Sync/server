using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosServer.Hubs;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Protos;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer;

public class SystemInfoService : IHostedService, IDisposable
{
    private readonly MareMetrics _mareMetrics;
    private readonly IServiceProvider _services;
    private readonly ILogger<SystemInfoService> _logger;
    private readonly IHubContext<MareHub> _hubContext;
    private Timer _timer;
    public SystemInfoDto SystemInfoDto { get; private set; } = new();

    public SystemInfoService(MareMetrics mareMetrics, IServiceProvider services, ILogger<SystemInfoService> logger, IHubContext<MareHub> hubContext)
    {
        _mareMetrics = mareMetrics;
        _services = services;
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
        _logger.LogInformation("ThreadPool: {workerThreads} workers available, {ioThreads} IO workers available", workerThreads, ioThreads);

        using var scope = _services.CreateScope();
        using var db = scope.ServiceProvider.GetService<MareDbContext>()!;

        _mareMetrics.SetGaugeTo(MetricsAPI.GaugeAvailableWorkerThreads, workerThreads);
        _mareMetrics.SetGaugeTo(MetricsAPI.GaugeAvailableIOWorkerThreads, ioThreads);

        var users = db.Users.Count(c => c.CharacterIdentification != null);

        SystemInfoDto = new SystemInfoDto()
        {
            CacheUsage = 0,
            CpuUsage = 0,
            RAMUsage = 0,
            NetworkIn = 0,
            NetworkOut = 0,
            OnlineUsers = users,
            UploadedFiles = 0
        };

        _hubContext.Clients.All.SendAsync(Api.OnUpdateSystemInfo, SystemInfoDto);
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