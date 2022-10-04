using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MareSynchronosStaticFilesServer;

public class CleanupService : IHostedService, IDisposable
{
    private readonly MareMetrics _metrics;
    private readonly ILogger<CleanupService> _logger;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private Timer? _timer;

    public CleanupService(MareMetrics metrics, ILogger<CleanupService> logger, IServiceProvider services, IConfiguration configuration)
    {
        _metrics = metrics;
        _logger = logger;
        _services = services;
        _configuration = configuration.GetRequiredSection("MareSynchronos");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cleanup Service started");

        _logger.LogInformation("Calculating initial files");

        DirectoryInfo dir = new DirectoryInfo(_configuration["CacheDirectory"]);
        var allFiles = dir.GetFiles();
        _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotalSize, allFiles.Sum(f => f.Length));
        _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotal, allFiles.Length);

        _logger.LogInformation("Initial file calculation finished, starting periodic cleanup task");

        _timer = new Timer(CleanUp, null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(10));

        return Task.CompletedTask;
    }

    private void CleanUp(object? state)
    {
        if (!int.TryParse(_configuration["UnusedFileRetentionPeriodInDays"], out var filesOlderThanDays))
        {
            filesOlderThanDays = 7;
        }

        using var scope = _services.CreateScope();
        using var dbContext = scope.ServiceProvider.GetService<MareDbContext>()!;

        _logger.LogInformation("Cleaning up files older than {filesOlderThanDays} days", filesOlderThanDays);

        try
        {
            var prevTime = DateTime.Now.Subtract(TimeSpan.FromDays(filesOlderThanDays));

            var allFiles = dbContext.Files.ToList();
            var cachedir = _configuration["CacheDirectory"];
            foreach (var file in allFiles.Where(f => f.Uploaded))
            {
                var fileName = Path.Combine(cachedir, file.Hash);
                var fi = new FileInfo(fileName);
                if (!fi.Exists)
                {
                    _logger.LogInformation("File does not exist anymore: {fileName}", fileName);
                    dbContext.Files.Remove(file);
                }
                else if (fi.LastAccessTime < prevTime)
                {
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotalSize, fi.Length);
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotal);
                    _logger.LogInformation("File outdated: {fileName}", fileName);
                    dbContext.Files.Remove(file);
                    fi.Delete();
                }
            }

            var allFilesHashes = new HashSet<string>(allFiles.Select(a => a.Hash.ToUpperInvariant()));
            DirectoryInfo dir = new DirectoryInfo(cachedir);
            var allFilesInDir = dir.GetFiles();
            foreach (var file in allFilesInDir)
            {
                if (!allFilesHashes.Contains(file.Name.ToUpperInvariant()))
                {
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotalSize, file.Length);
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotal);
                    file.Delete();
                    _logger.LogInformation("File not in DB, deleting: {fileName}", file.FullName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during file cleanup");
        }

        var cacheSizeLimitInGiB = _configuration.GetValue<double>("CacheSizeHardLimitInGiB", -1);

        try
        {
            if (cacheSizeLimitInGiB > 0)
            {
                _logger.LogInformation("Cleaning up files beyond the cache size limit");
                var allLocalFiles = Directory.EnumerateFiles(_configuration["CacheDirectory"])
                    .Select(f => new FileInfo(f)).ToList().OrderBy(f => f.LastAccessTimeUtc).ToList();
                var totalCacheSizeInBytes = allLocalFiles.Sum(s => s.Length);
                long cacheSizeLimitInBytes = (long)(cacheSizeLimitInGiB * 1024 * 1024 * 1024);
                HashSet<string> removedHashes = new();
                while (totalCacheSizeInBytes > cacheSizeLimitInBytes && allLocalFiles.Any())
                {
                    var oldestFile = allLocalFiles.First();
                    removedHashes.Add(oldestFile.Name.ToLower());
                    allLocalFiles.Remove(oldestFile);
                    totalCacheSizeInBytes -= oldestFile.Length;
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotalSize, oldestFile.Length);
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotal);
                    oldestFile.Delete();
                }

                dbContext.Files.RemoveRange(dbContext.Files.Where(f => removedHashes.Contains(f.Hash.ToLower())));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during cache size limit cleanup");
        }

        _logger.LogInformation($"Cleanup complete");

        dbContext.SaveChanges();
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
