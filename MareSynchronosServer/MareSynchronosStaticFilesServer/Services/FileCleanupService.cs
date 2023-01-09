using ByteSizeLib;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosStaticFilesServer.Utils;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosStaticFilesServer.Services;

public class FileCleanupService : IHostedService
{
    private readonly MareMetrics _metrics;
    private readonly ILogger<FileCleanupService> _logger;
    private readonly IServiceProvider _services;
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configuration;
    private readonly bool _isMainServer;
    private readonly string _cacheDir;
    private CancellationTokenSource _cleanupCts;

    public FileCleanupService(MareMetrics metrics, ILogger<FileCleanupService> logger, IServiceProvider services, IConfigurationService<StaticFilesServerConfiguration> configuration)
    {
        _metrics = metrics;
        _logger = logger;
        _services = services;
        _configuration = configuration;
        _isMainServer = configuration.IsMain;
        _cacheDir = _configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cleanup Service started");

        _cleanupCts = new();

        _ = CleanUpTask(_cleanupCts.Token);

        return Task.CompletedTask;
    }

    public async Task CleanUpTask(CancellationToken ct)
    {
        _logger.LogInformation("Starting periodic cleanup task");

        while (!ct.IsCancellationRequested)
        {
            DirectoryInfo dir = new(_cacheDir);
            var allFiles = dir.GetFiles("*", SearchOption.AllDirectories);
            _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotalSize, allFiles.Sum(f => f.Length));
            _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotal, allFiles.Length);

            using var scope = _services.CreateScope();
            using var dbContext = scope.ServiceProvider.GetService<MareDbContext>()!;

            await CleanUpOutdatedFiles(dbContext, ct).ConfigureAwait(false);

            CleanUpFilesBeyondSizeLimit(dbContext, ct);

            if (_isMainServer)
            {
                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            var now = DateTime.Now;
            TimeOnly currentTime = new(now.Hour, now.Minute, now.Second);
            TimeOnly futureTime = new(now.Hour, now.Minute - now.Minute % 10, 0);
            var span = futureTime.AddMinutes(10) - currentTime;

            _logger.LogInformation("File Cleanup Complete, next run at {date}", now.Add(span));
            await Task.Delay(span, ct).ConfigureAwait(false);
        }
    }

    private void CleanUpFilesBeyondSizeLimit(MareDbContext dbContext, CancellationToken ct)
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
                if (_isMainServer)
                {
                    FileCache f = new() { Hash = oldestFile.Name.ToUpperInvariant() };
                    dbContext.Entry(f).State = EntityState.Deleted;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during cache size limit cleanup");
        }
    }

    private async Task CleanUpOutdatedFiles(MareDbContext dbContext, CancellationToken ct)
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

            // clean up files in DB but not on disk or last access is expired
            var prevTime = DateTime.Now.Subtract(TimeSpan.FromDays(unusedRetention));
            var prevTimeForcedDeletion = DateTime.Now.Subtract(TimeSpan.FromHours(forcedDeletionAfterHours));
            var allFiles = await dbContext.Files.ToListAsync().ConfigureAwait(false);
            int fileCounter = 0;
            foreach (var fileCache in allFiles.Where(f => f.Uploaded))
            {
                var file = FilePathUtil.GetFileInfoForHash(_cacheDir, fileCache.Hash);
                bool fileDeleted = false;
                if (file == null && _isMainServer)
                {
                    _logger.LogInformation("File does not exist anymore: {fileName}", fileCache.Hash);
                    dbContext.Files.Remove(fileCache);
                    fileDeleted = true;
                }
                else if (file != null && file.LastAccessTime < prevTime)
                {
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotalSize, file.Length);
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotal);
                    _logger.LogInformation("File outdated: {fileName}, {fileSize}MiB", file.Name, ByteSize.FromBytes(file.Length).MebiBytes);
                    file.Delete();
                    if (_isMainServer)
                    {
                        fileDeleted = true;
                        dbContext.Files.Remove(fileCache);
                    }
                }
                else if (file != null && forcedDeletionAfterHours > 0 && file.LastWriteTime < prevTimeForcedDeletion)
                {
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotalSize, file.Length);
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotal);
                    _logger.LogInformation("File forcefully deleted: {fileName}, {fileSize}MiB", file.Name, ByteSize.FromBytes(file.Length).MebiBytes);
                    file.Delete();
                    if (_isMainServer)
                    {
                        fileDeleted = true;
                        dbContext.Files.Remove(fileCache);
                    }
                }

                if (_isMainServer && !fileDeleted && file != null && fileCache.Size == 0)
                {
                    _logger.LogInformation("Setting File Size of " + fileCache.Hash + " to " + file.Length);
                    fileCache.Size = file.Length;
                    // commit every 1000 files to db
                    if (fileCounter % 1000 == 0) await dbContext.SaveChangesAsync().ConfigureAwait(false);
                }

                fileCounter++;

                ct.ThrowIfCancellationRequested();
            }

            // clean up files that are on disk but not in DB for some reason
            CleanUpOrphanedFiles(allFiles, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during file cleanup of old files");
        }
    }

    private void CleanUpOrphanedFiles(List<FileCache> allFiles, CancellationToken ct)
    {
        if (_isMainServer)
        {
            var allFilesHashes = new HashSet<string>(allFiles.Select(a => a.Hash.ToUpperInvariant()), StringComparer.Ordinal);
            DirectoryInfo dir = new(_cacheDir);
            var allFilesInDir = dir.GetFiles("*", SearchOption.AllDirectories);
            foreach (var file in allFilesInDir)
            {
                if (!allFilesHashes.Contains(file.Name.ToUpperInvariant()))
                {
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotalSize, file.Length);
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotal);
                    file.Delete();
                    _logger.LogInformation("File not in DB, deleting: {fileName}", file.Name);
                }

                ct.ThrowIfCancellationRequested();
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupCts.Cancel();

        return Task.CompletedTask;
    }
}
