using ByteSizeLib;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosStaticFilesServer.Services;

public class ShardFileCleanupService : IHostedService
{
    private readonly string _cacheDir;
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configuration;
    private readonly ILogger<MainFileCleanupService> _logger;
    private readonly MareMetrics _metrics;
    private CancellationTokenSource _cleanupCts;

    public ShardFileCleanupService(MareMetrics metrics, ILogger<MainFileCleanupService> logger, IConfigurationService<StaticFilesServerConfiguration> configuration)
    {
        _metrics = metrics;
        _logger = logger;
        _configuration = configuration;
        _cacheDir = _configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));
    }

    public async Task CleanUpTask(CancellationToken ct)
    {
        _logger.LogInformation("Starting periodic cleanup task");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                DirectoryInfo dir = new(_cacheDir);
                var allFiles = dir.GetFiles("*", SearchOption.AllDirectories);
                _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotalSize, allFiles.Sum(f => f.Length));
                _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotal, allFiles.Length);

                CleanUpOutdatedFiles(ct);

                CleanUpFilesBeyondSizeLimit(ct);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error during cleanup task");
            }

            var cleanupCheckMinutes = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.CleanupCheckInMinutes), 15);

            var now = DateTime.Now;
            TimeOnly currentTime = new(now.Hour, now.Minute, now.Second);
            TimeOnly futureTime = new(now.Hour, now.Minute - now.Minute % cleanupCheckMinutes, 0);
            var span = futureTime.AddMinutes(cleanupCheckMinutes) - currentTime;

            _logger.LogInformation("File Cleanup Complete, next run at {date}", now.Add(span));
            await Task.Delay(span, ct).ConfigureAwait(false);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cleanup Service started");

        _cleanupCts = new();

        _ = CleanUpTask(_cleanupCts.Token);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupCts.Cancel();

        return Task.CompletedTask;
    }

    private void CleanUpFilesBeyondSizeLimit(CancellationToken ct)
    {
        var sizeLimit = _configuration.GetValueOrDefault<double>(nameof(StaticFilesServerConfiguration.CacheSizeHardLimitInGiB), -1);
        if (sizeLimit <= 0)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Cleaning up files beyond the cache size limit of {cacheSizeLimit} GiB", sizeLimit);
            var allLocalFiles = Directory.EnumerateFiles(_cacheDir, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("dl", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f)).ToList()
                .OrderBy(f => f.LastAccessTimeUtc).ToList();
            var totalCacheSizeInBytes = allLocalFiles.Sum(s => s.Length);
            long cacheSizeLimitInBytes = (long)ByteSize.FromGibiBytes(sizeLimit).Bytes;
            while (totalCacheSizeInBytes > cacheSizeLimitInBytes && allLocalFiles.Any() && !ct.IsCancellationRequested)
            {
                var oldestFile = allLocalFiles[0];
                allLocalFiles.Remove(oldestFile);
                totalCacheSizeInBytes -= oldestFile.Length;
                _metrics.DecGauge(MetricsAPI.GaugeFilesTotalSize, oldestFile.Length);
                _metrics.DecGauge(MetricsAPI.GaugeFilesTotal);
                _logger.LogInformation("Deleting {oldestFile} with size {size}MiB", oldestFile.FullName, ByteSize.FromBytes(oldestFile.Length).MebiBytes);
                oldestFile.Delete();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during cache size limit cleanup");
        }
    }

    private void CleanUpOutdatedFiles(CancellationToken ct)
    {
        try
        {
            var unusedRetention = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UnusedFileRetentionPeriodInDays), 14);
            var forcedDeletionAfterHours = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.ForcedDeletionOfFilesAfterHours), -1);

            _logger.LogInformation("Cleaning up files older than {filesOlderThanDays} days", unusedRetention);
            if (forcedDeletionAfterHours > 0)
            {
                _logger.LogInformation("Cleaning up files written to longer than {hours}h ago", forcedDeletionAfterHours);
            }

            var prevTime = DateTime.Now.Subtract(TimeSpan.FromDays(unusedRetention));
            var prevTimeForcedDeletion = DateTime.Now.Subtract(TimeSpan.FromHours(forcedDeletionAfterHours));
            DirectoryInfo dir = new(_cacheDir);
            var allFilesInDir = dir.GetFiles("*", SearchOption.AllDirectories)
                .Where(f => !f.Name.EndsWith("dl", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var file in allFilesInDir)
            {
                if (file.LastAccessTime < prevTime)
                {
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotalSize, file.Length);
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotal);
                    _logger.LogInformation("File outdated: {fileName}, {fileSize}MiB", file.Name, ByteSize.FromBytes(file.Length).MebiBytes);
                    file.Delete();
                }
                else if (forcedDeletionAfterHours > 0 && file.LastWriteTime < prevTimeForcedDeletion)
                {
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotalSize, file.Length);
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotal);
                    _logger.LogInformation("File forcefully deleted: {fileName}, {fileSize}MiB", file.Name, ByteSize.FromBytes(file.Length).MebiBytes);
                    file.Delete();
                }
                else if (file.Length == 0 && !string.Equals(file.Extension, ".dl", StringComparison.OrdinalIgnoreCase))
                {
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotalSize, file.Length);
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotal);
                    _logger.LogInformation("File with size 0 deleted: {filename}", file.Name);
                    file.Delete();
                }

                ct.ThrowIfCancellationRequested();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during file cleanup of old files");
        }
    }
}
