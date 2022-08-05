using System;
using System.Threading;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosServer.Hubs;
using MareSynchronosServer.Metrics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer;

public class SystemInfoService : IHostedService, IDisposable
{
    private readonly ILogger<SystemInfoService> _logger;
    private readonly IHubContext<MareHub> _hubContext;
    private Timer _timer;
    public SystemInfoDto SystemInfoDto { get; private set; } = new();

    public SystemInfoService(ILogger<SystemInfoService> logger, IHubContext<MareHub> hubContext)
    {
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
        SystemInfoDto = new SystemInfoDto()
        {
            CacheUsage = 0,
            CpuUsage = 0,
            RAMUsage = 0,
            NetworkIn = 0,
            NetworkOut = 0,
            OnlineUsers = (int)MareMetrics.AuthorizedConnections.Value,
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