using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using static MareSynchronosShared.Protos.MetricsService;

namespace MareSynchronosStaticFilesServer;

public class CleanupService : IHostedService, IDisposable
{
    private readonly MetricsServiceClient _metrics;
    private readonly ILogger<CleanupService> _logger;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private Timer? _timer;

    public CleanupService(MetricsServiceClient metrics, ILogger<CleanupService> logger, IServiceProvider services, IConfiguration configuration)
    {
        _metrics = metrics;
        _logger = logger;
        _services = services;
        _configuration = configuration;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cleanup Service started");

        _timer = new Timer(CleanUp, null, TimeSpan.Zero, TimeSpan.FromMinutes(10));

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
                    _metrics.DecGauge(new() { GaugeName = MetricsAPI.GaugeFilesTotalSize, Value = fi.Length });
                    _metrics.DecGauge(new() { GaugeName = MetricsAPI.GaugeFilesTotal, Value = 1 });
                    _logger.LogInformation("File outdated: {fileName}", fileName);
                    dbContext.Files.Remove(file);
                    fi.Delete();
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
                var allLocalFiles = Directory.EnumerateFiles(_configuration["CacheDirectory"]).Select(f => new FileInfo(f)).ToList().OrderBy(f => f.LastAccessTimeUtc).ToList();
                var totalCacheSizeInBytes = allLocalFiles.Sum(s => s.Length);
                long cacheSizeLimitInBytes = (long)(cacheSizeLimitInGiB * 1024 * 1024 * 1024);
                HashSet<string> removedHashes = new();
                while (totalCacheSizeInBytes > cacheSizeLimitInBytes && allLocalFiles.Any())
                {
                    var oldestFile = allLocalFiles.First();
                    removedHashes.Add(oldestFile.Name.ToLower());
                    allLocalFiles.Remove(oldestFile);
                    totalCacheSizeInBytes -= oldestFile.Length;
                    _metrics.DecGauge(new() { GaugeName = MetricsAPI.GaugeFilesTotalSize, Value = oldestFile.Length });
                    _metrics.DecGauge(new() { GaugeName = MetricsAPI.GaugeFilesTotal, Value = 1 });
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
