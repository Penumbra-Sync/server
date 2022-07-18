using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
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

        _timer = new Timer(CalculateCpuUsage, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        return Task.CompletedTask;
    }

    private void CalculateCpuUsage(object state)
    {
        var startTime = DateTime.UtcNow;
        double startCpuUsage = 0;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                startCpuUsage += process.TotalProcessorTime.TotalMilliseconds;
            }
            catch { }
        }
        var networkOut = NetworkInterface.GetAllNetworkInterfaces().Sum(n => n.GetIPStatistics().BytesSent);
        var networkIn = NetworkInterface.GetAllNetworkInterfaces().Sum(n => n.GetIPStatistics().BytesReceived);
        var stopWatch = new Stopwatch();
        stopWatch.Start();

        Thread.Sleep(TimeSpan.FromSeconds(5));

        stopWatch.Stop();
        var endTime = DateTime.UtcNow;
        double endCpuUsage = 0;
        long ramUsage = 0;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                endCpuUsage += process.TotalProcessorTime.TotalMilliseconds;
                ramUsage += process.WorkingSet64;
            }
            catch { }
        }
        var endNetworkOut = NetworkInterface.GetAllNetworkInterfaces().Sum(n => n.GetIPStatistics().BytesSent);
        var endNetworkIn = NetworkInterface.GetAllNetworkInterfaces().Sum(n => n.GetIPStatistics().BytesReceived);

        var totalMsPassed = (endTime - startTime).TotalMilliseconds;
        var totalSPassed = (endTime - startTime).TotalSeconds;

        var cpuUsedMs = (endCpuUsage - startCpuUsage);
        var cpuUsageTotal = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);
        var bytesSent = endNetworkOut - networkOut;
        var bytesReceived = endNetworkIn - networkIn;


        var usedRAM = Process.GetCurrentProcess().WorkingSet64 + Process.GetProcessesByName("sqlservr").FirstOrDefault()?.WorkingSet64 ?? 0;

        var cpuUsage = cpuUsageTotal * 100;
        var totalNetworkOut = bytesSent / totalSPassed;
        var totalNetworkIn = bytesReceived / totalSPassed;

        MareMetrics.NetworkIn.Set(totalNetworkIn);
        MareMetrics.NetworkOut.Set(totalNetworkOut);
        MareMetrics.CPUUsage.Set(cpuUsage);
        MareMetrics.RAMUsage.Set(usedRAM);

        SystemInfoDto = new SystemInfoDto()
        {
            CacheUsage = 0,
            CpuUsage = cpuUsage,
            RAMUsage = 0,
            NetworkIn = totalNetworkIn,
            NetworkOut = totalNetworkOut,
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