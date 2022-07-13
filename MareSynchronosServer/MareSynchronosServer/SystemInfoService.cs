using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosServer.Data;
using MareSynchronosServer.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer;

public class SystemInfoService : IHostedService, IDisposable
{
    private readonly ILogger<SystemInfoService> _logger;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly IHubContext<MareHub> _hubContext;
    private Timer _timer;
    public SystemInfoDto SystemInfoDto { get; private set; } = new();

    public SystemInfoService(ILogger<SystemInfoService> logger, IServiceProvider services,
        IConfiguration configuration, IHubContext<MareHub> hubContext)
    {
        _logger = logger;
        _services = services;
        _configuration = configuration;
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

        using var scope = _services.CreateScope();
        var dbContext = scope.ServiceProvider.GetService<MareDbContext>()!;

        int uploadedFiles = 0;
        var loggedInUsers = dbContext.Users.Count(u => !string.IsNullOrEmpty(u.CharacterIdentification));
        var localCacheSize = Directory.EnumerateFiles(_configuration["CacheDirectory"])
            .ToList().Sum(f =>
            {
                uploadedFiles++;
                try
                {
                    return new FileInfo(f).Length;
                }
                catch
                {
                    return 0;
                }
            });

        var totalNetworkOut = bytesSent / totalSPassed;
        var totalNetworkIn = bytesReceived / totalSPassed;
        var cpuUsage = cpuUsageTotal * 100;
        var usedRAM = Process.GetCurrentProcess().WorkingSet64 + Process.GetProcessesByName("sqlservr").FirstOrDefault()?.WorkingSet64 ?? 0;

        SystemInfoDto = new SystemInfoDto()
        {
            CacheUsage = localCacheSize,
            CpuUsage = cpuUsage,
            RAMUsage = usedRAM,
            NetworkIn = totalNetworkIn,
            NetworkOut = totalNetworkOut,
            OnlineUsers = loggedInUsers,
            UploadedFiles = uploadedFiles
        };

        _hubContext.Clients.All.SendAsync(Api.OnUpdateSystemInfo, SystemInfoDto);

        _logger.LogInformation($"CPU:{cpuUsage:0.00}%, RAM Used:{(double)usedRAM / 1024 / 1024 / 1024:0.00}GB, Cache:{(double)localCacheSize / 1024 / 1024 / 1024:0.00}GB, Users:{loggedInUsers}, NetworkIn:{totalNetworkIn / 1024 / 1024:0.00}MB/s, NetworkOut:{totalNetworkOut / 1024 / 1024:0.00}MB/s");
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