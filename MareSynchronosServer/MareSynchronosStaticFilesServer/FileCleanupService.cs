using ByteSizeLib;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using System.Globalization;

namespace MareSynchronosStaticFilesServer;

public class FileCleanupService : IHostedService
{
    private readonly MareMetrics _metrics;
    private readonly ILogger<FileCleanupService> _logger;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly bool _isMainServer;
    private readonly string _cacheDir;
    private CancellationTokenSource _cleanupCts;

    public FileCleanupService(MareMetrics metrics, ILogger<FileCleanupService> logger, IServiceProvider services, IConfiguration configuration)
    {
        _metrics = metrics;
        _logger = logger;
        _services = services;
        _configuration = configuration.GetRequiredSection("MareSynchronos");
        _isMainServer = !string.IsNullOrEmpty(_configuration.GetValue("RemoteCacheSource", string.Empty));
        _cacheDir = _configuration.GetValue<string>("CacheDirectory");
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

            CleanUpOutdatedFiles(dbContext, ct);

            CleanUpFilesBeyondSizeLimit(dbContext, ct);

            if (_isMainServer)
            {
                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            _logger.LogInformation("File Cleanup Complete, next run at {date}", DateTime.Now.Add(TimeSpan.FromMinutes(10)));
            await Task.Delay(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        }
    }

    private void CleanUpFilesBeyondSizeLimit(MareDbContext dbContext, CancellationToken ct)
    {
        var cacheSizeLimitInGiB = _configuration.GetValue<double>("CacheSizeHardLimitInGiB", -1);

        if (cacheSizeLimitInGiB <= 0)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Cleaning up files beyond the cache size limit of {cacheSizeLimit} GiB", cacheSizeLimitInGiB);
            var allLocalFiles = Directory.EnumerateFiles(_cacheDir, "*", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f)).ToList()
                .OrderBy(f => f.LastAccessTimeUtc).ToList();
            var totalCacheSizeInBytes = allLocalFiles.Sum(s => s.Length);
            long cacheSizeLimitInBytes = (long)ByteSize.FromGibiBytes(cacheSizeLimitInGiB).Bytes;
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
                    dbContext.Entry(f).State = Microsoft.EntityFrameworkCore.EntityState.Deleted;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during cache size limit cleanup");
        }
    }

    private void CleanUpOutdatedFiles(MareDbContext dbContext, CancellationToken ct)
    {
        try
        {
            if (!int.TryParse(_configuration["UnusedFileRetentionPeriodInDays"], CultureInfo.InvariantCulture, out int filesOlderThanDays))
            {
                filesOlderThanDays = 7;
            }

            _logger.LogInformation("Cleaning up files older than {filesOlderThanDays} days", filesOlderThanDays);

            // clean up files in DB but not on disk or last access is expired
            var prevTime = DateTime.Now.Subtract(TimeSpan.FromDays(filesOlderThanDays));
            var allFiles = dbContext.Files.ToList();
            foreach (var fileCache in allFiles.Where(f => f.Uploaded))
            {
                var file = FilePathUtil.GetFileInfoForHash(_cacheDir, fileCache.Hash);
                if (file == null && _isMainServer)
                {
                    _logger.LogInformation("File does not exist anymore: {fileName}", fileCache.Hash);
                    dbContext.Files.Remove(fileCache);
                }
                else if (file != null && file.LastAccessTime < prevTime)
                {
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotalSize, file.Length);
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotal);
                    _logger.LogInformation("File outdated: {fileName}, {fileSize}MiB", file.Name, ByteSize.FromBytes(file.Length).MebiBytes);
                    file.Delete();
                    if (_isMainServer)
                        dbContext.Files.Remove(fileCache);
                }

                ct.ThrowIfCancellationRequested();
            }

            // clean up files that are on disk but not in DB for some reason
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
                        _logger.LogInformation("File not in DB, deleting: {fileName}", file.FullName);
                    }

                    ct.ThrowIfCancellationRequested();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during file cleanup of old files");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupCts.Cancel();

        return Task.CompletedTask;
    }
}
