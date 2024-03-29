﻿using ByteSizeLib;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosStaticFilesServer.Utils;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosStaticFilesServer.Services;

public class MainFileCleanupService : IHostedService
{
    private readonly string _cacheDir;
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configuration;
    private readonly ILogger<MainFileCleanupService> _logger;
    private readonly MareMetrics _metrics;
    private readonly IServiceProvider _services;
    private CancellationTokenSource _cleanupCts;

    public MainFileCleanupService(MareMetrics metrics, ILogger<MainFileCleanupService> logger,
        IServiceProvider services, IConfigurationService<StaticFilesServerConfiguration> configuration)
    {
        _metrics = metrics;
        _logger = logger;
        _services = services;
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

                using var scope = _services.CreateScope();
                using var dbContext = scope.ServiceProvider.GetService<MareDbContext>()!;

                await CleanUpOutdatedFiles(dbContext, ct).ConfigureAwait(false);

                CleanUpFilesBeyondSizeLimit(dbContext, ct);

                CleanUpStuckUploads(dbContext);

                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
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
                FileCache f = new() { Hash = oldestFile.Name.ToUpperInvariant() };
                dbContext.Entry(f).State = EntityState.Deleted;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during cache size limit cleanup");
        }
    }

    private void CleanUpOrphanedFiles(List<FileCache> allFiles, FileInfo[] allPhysicalFiles, CancellationToken ct)
    {
        var allFilesHashes = new HashSet<string>(allFiles.Select(a => a.Hash.ToUpperInvariant()), StringComparer.Ordinal);
        foreach (var file in allPhysicalFiles)
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
            DirectoryInfo dir = new(_cacheDir);
            var allFilesInDir = dir.GetFiles("*", SearchOption.AllDirectories);
            var files = dbContext.Files.OrderBy(f => f.Hash);
            List<FileCache> allFiles = await dbContext.Files.ToListAsync(ct).ConfigureAwait(false);
            int fileCounter = 0;

            foreach (var fileCache in allFiles.Where(f => f.Uploaded))
            {
                bool fileDeleted = false;

                var file = FilePathUtil.GetFileInfoForHash(_cacheDir, fileCache.Hash);
                if (file == null)
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
                    fileDeleted = true;
                    dbContext.Files.Remove(fileCache);
                }
                else if (file != null && forcedDeletionAfterHours > 0 && file.LastWriteTime < prevTimeForcedDeletion)
                {
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotalSize, file.Length);
                    _metrics.DecGauge(MetricsAPI.GaugeFilesTotal);
                    _logger.LogInformation("File forcefully deleted: {fileName}, {fileSize}MiB", file.Name, ByteSize.FromBytes(file.Length).MebiBytes);
                    file.Delete();
                    fileDeleted = true;
                    dbContext.Files.Remove(fileCache);
                }

                if (!fileDeleted && file != null && fileCache.Size == 0)
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
            CleanUpOrphanedFiles(allFiles, allFilesInDir, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during file cleanup of old files");
        }
    }

    private void CleanUpStuckUploads(MareDbContext dbContext)
    {
        var pastTime = DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(10));
        var stuckUploads = dbContext.Files.Where(f => !f.Uploaded && f.UploadDate < pastTime);
        dbContext.Files.RemoveRange(stuckUploads);
    }
}