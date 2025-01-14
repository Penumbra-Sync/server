using ByteSizeLib;
using K4os.Compression.LZ4.Legacy;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using MareSynchronosStaticFilesServer.Utils;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosStaticFilesServer.Services;

public class MainFileCleanupService : IHostedService
{
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configuration;
    private readonly IDbContextFactory<MareDbContext> _dbContextFactory;
    private readonly ILogger<MainFileCleanupService> _logger;
    private readonly MareMetrics _metrics;
    private CancellationTokenSource _cleanupCts;

    public MainFileCleanupService(MareMetrics metrics, ILogger<MainFileCleanupService> logger,
        IConfigurationService<StaticFilesServerConfiguration> configuration,
        IDbContextFactory<MareDbContext> dbContextFactory)
    {
        _metrics = metrics;
        _logger = logger;
        _configuration = configuration;
        _dbContextFactory = dbContextFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cleanup Service started");

        _cleanupCts = new();

        _ = Task.Run(() => CleanUpTask(_cleanupCts.Token)).ConfigureAwait(false);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupCts.Cancel();

        return Task.CompletedTask;
    }

    private List<FileInfo> CleanUpFilesBeyondSizeLimit(List<FileInfo> files, double sizeLimit, bool deleteFromDb, MareDbContext dbContext, CancellationToken ct)
    {
        if (sizeLimit <= 0)
        {
            return [];
        }

        try
        {
            _logger.LogInformation("Cleaning up files beyond the cache size limit of {cacheSizeLimit} GiB", sizeLimit);
            var allLocalFiles = files
                .OrderBy(f => f.LastAccessTimeUtc).ToList();
            var totalCacheSizeInBytes = allLocalFiles.Sum(s => s.Length);
            long cacheSizeLimitInBytes = (long)ByteSize.FromGibiBytes(sizeLimit).Bytes;
            while (totalCacheSizeInBytes > cacheSizeLimitInBytes && allLocalFiles.Count != 0 && !ct.IsCancellationRequested)
            {
                var oldestFile = allLocalFiles[0];
                allLocalFiles.RemoveAt(0);
                totalCacheSizeInBytes -= oldestFile.Length;
                _logger.LogInformation("Deleting {oldestFile} with size {size}MiB", oldestFile.FullName, ByteSize.FromBytes(oldestFile.Length).MebiBytes);
                oldestFile.Delete();
                FileCache f = new() { Hash = oldestFile.Name.ToUpperInvariant() };
                if (deleteFromDb)
                    dbContext.Entry(f).State = EntityState.Deleted;
            }

            return allLocalFiles;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during cache size limit cleanup");
        }

        return [];
    }

    private List<FileInfo> CleanUpOrphanedFiles(List<FileCache> allFiles, List<FileInfo> allPhysicalFiles, CancellationToken ct)
    {
        var allFilesHashes = new HashSet<string>(allFiles.Select(a => a.Hash.ToUpperInvariant()), StringComparer.Ordinal);
        foreach (var file in allPhysicalFiles.ToList())
        {
            if (!allFilesHashes.Contains(file.Name.ToUpperInvariant()))
            {
                _metrics.DecGauge(MetricsAPI.GaugeFilesTotalSize, file.Length);
                _metrics.DecGauge(MetricsAPI.GaugeFilesTotal);
                file.Delete();
                _logger.LogInformation("File not in DB, deleting: {fileName}", file.Name);
                allPhysicalFiles.Remove(file);
            }

            ct.ThrowIfCancellationRequested();
        }

        return allPhysicalFiles;
    }

    private async Task<List<FileInfo>> CleanUpOutdatedFiles(string dir, List<FileInfo> allFilesInDir, int unusedRetention, int forcedDeletionAfterHours,
        bool deleteFromDb, MareDbContext dbContext, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Cleaning up files older than {filesOlderThanDays} days", unusedRetention);
            if (forcedDeletionAfterHours > 0)
            {
                _logger.LogInformation("Cleaning up files written to longer than {hours}h ago", forcedDeletionAfterHours);
            }

            // clean up files in DB but not on disk or last access is expired
            var prevTime = DateTime.Now.Subtract(TimeSpan.FromDays(unusedRetention));
            var prevTimeForcedDeletion = DateTime.Now.Subtract(TimeSpan.FromHours(forcedDeletionAfterHours));
            List<FileCache> allDbFiles = await dbContext.Files.ToListAsync(ct).ConfigureAwait(false);
            List<string> removedFileHashes;

            if (!deleteFromDb)
            {
                removedFileHashes = CleanupViaFiles(allFilesInDir, forcedDeletionAfterHours, prevTime, prevTimeForcedDeletion, ct);
            }
            else
            {
                removedFileHashes = await CleanupViaDb(dir, forcedDeletionAfterHours, dbContext, prevTime, prevTimeForcedDeletion, allDbFiles, ct).ConfigureAwait(false);
            }

            // clean up files that are on disk but not in DB anymore
            return CleanUpOrphanedFiles(allDbFiles, allFilesInDir.Where(c => !removedFileHashes.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during file cleanup of old files");
        }

        return [];
    }

    private void CleanUpStuckUploads(MareDbContext dbContext)
    {
        var pastTime = DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(20));
        var stuckUploads = dbContext.Files.Where(f => !f.Uploaded && f.UploadDate < pastTime);
        dbContext.Files.RemoveRange(stuckUploads);
    }

    private async Task CleanUpTask(CancellationToken ct)
    {
        InitializeGauges();

        while (!ct.IsCancellationRequested)
        {
            var cleanupCheckMinutes = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.CleanupCheckInMinutes), 15);
            bool useColdStorage = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false);
            var hotStorageDir = _configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));
            var coldStorageDir = _configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.ColdStorageDirectory));
            using var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

            _logger.LogInformation("Running File Cleanup Task");

            try
            {
                using CancellationTokenSource timedCts = new();
                timedCts.CancelAfter(TimeSpan.FromMinutes(cleanupCheckMinutes - 1));
                using var linkedTokenCts = CancellationTokenSource.CreateLinkedTokenSource(timedCts.Token, ct);
                var linkedToken = linkedTokenCts.Token;

                DirectoryInfo dirHotStorage = new(hotStorageDir);
                _logger.LogInformation("File Cleanup Task gathering hot storage files");
                var allFilesInHotStorage = dirHotStorage.GetFiles("*", SearchOption.AllDirectories).ToList();

                var unusedRetention = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UnusedFileRetentionPeriodInDays), 14);
                var forcedDeletionAfterHours = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.ForcedDeletionOfFilesAfterHours), -1);
                var sizeLimit = _configuration.GetValueOrDefault<double>(nameof(StaticFilesServerConfiguration.CacheSizeHardLimitInGiB), -1);

                _logger.LogInformation("File Cleanup Task cleaning up outdated hot storage files");
                var remainingHotFiles = await CleanUpOutdatedFiles(hotStorageDir, allFilesInHotStorage, unusedRetention, forcedDeletionAfterHours,
                    deleteFromDb: !useColdStorage, dbContext: dbContext,
                    ct: linkedToken).ConfigureAwait(false);

                _logger.LogInformation("File Cleanup Task cleaning up hot storage file beyond size limit");
                var finalRemainingHotFiles = CleanUpFilesBeyondSizeLimit(remainingHotFiles, sizeLimit,
                    deleteFromDb: !useColdStorage, dbContext: dbContext,
                    ct: linkedToken);

                if (useColdStorage)
                {
                    DirectoryInfo dirColdStorage = new(coldStorageDir);
                    _logger.LogInformation("File Cleanup Task gathering cold storage files");
                    var allFilesInColdStorageDir = dirColdStorage.GetFiles("*", SearchOption.AllDirectories).ToList();

                    var coldStorageRetention = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.ColdStorageUnusedFileRetentionPeriodInDays), 60);
                    var coldStorageSize = _configuration.GetValueOrDefault<double>(nameof(StaticFilesServerConfiguration.ColdStorageSizeHardLimitInGiB), -1);

                    // clean up cold storage
                    _logger.LogInformation("File Cleanup Task cleaning up outdated cold storage files");
                    var remainingColdFiles = await CleanUpOutdatedFiles(coldStorageDir, allFilesInColdStorageDir, coldStorageRetention, forcedDeletionAfterHours: -1,
                        deleteFromDb: true, dbContext: dbContext,
                        ct: linkedToken).ConfigureAwait(false);
                    _logger.LogInformation("File Cleanup Task cleaning up cold storage file beyond size limit");
                    var finalRemainingColdFiles = CleanUpFilesBeyondSizeLimit(remainingColdFiles, coldStorageSize,
                        deleteFromDb: true, dbContext: dbContext,
                        ct: linkedToken);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error during cleanup task");
            }
            finally
            {
                CleanUpStuckUploads(dbContext);

                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            if (useColdStorage)
            {
                DirectoryInfo dirColdStorageAfterCleanup = new(coldStorageDir);
                var allFilesInColdStorageAfterCleanup = dirColdStorageAfterCleanup.GetFiles("*", SearchOption.AllDirectories).ToList();

                _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotalSizeColdStorage, allFilesInColdStorageAfterCleanup.Sum(f => { try { return f.Length; } catch { return 0; } }));
                _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotalColdStorage, allFilesInColdStorageAfterCleanup.Count);
            }

            DirectoryInfo dirHotStorageAfterCleanup = new(hotStorageDir);
            var allFilesInHotStorageAfterCleanup = dirHotStorageAfterCleanup.GetFiles("*", SearchOption.AllDirectories).ToList();

            _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotalSize, allFilesInHotStorageAfterCleanup.Sum(f => { try { return f.Length; } catch { return 0; } }));
            _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotal, allFilesInHotStorageAfterCleanup.Count);

            var now = DateTime.Now;
            TimeOnly currentTime = new(now.Hour, now.Minute, now.Second);
            TimeOnly futureTime = new(now.Hour, now.Minute - now.Minute % cleanupCheckMinutes, 0);
            var span = futureTime.AddMinutes(cleanupCheckMinutes) - currentTime;

            _logger.LogInformation("File Cleanup Complete, next run at {date}", now.Add(span));
            await Task.Delay(span, ct).ConfigureAwait(false);
        }
    }
    private async Task<List<string>> CleanupViaDb(string dir, int forcedDeletionAfterHours,
        MareDbContext dbContext, DateTime lastAccessCutoffTime, DateTime forcedDeletionCutoffTime, List<FileCache> allDbFiles, CancellationToken ct)
    {
        int fileCounter = 0;
        List<string> removedFileHashes = new();
        foreach (var fileCache in allDbFiles.Where(f => f.Uploaded))
        {
            bool deleteCurrentFile = false;
            var file = FilePathUtil.GetFileInfoForHash(dir, fileCache.Hash);
            if (file == null)
            {
                _logger.LogInformation("File does not exist anymore: {fileName}", fileCache.Hash);
                deleteCurrentFile = true;
            }
            else if (file != null && file.LastAccessTime < lastAccessCutoffTime)
            {
                _logger.LogInformation("File outdated: {fileName}, {fileSize}MiB", file.Name, ByteSize.FromBytes(file.Length).MebiBytes);
                deleteCurrentFile = true;
            }
            else if (file != null && forcedDeletionAfterHours > 0 && file.LastWriteTime < forcedDeletionCutoffTime)
            {
                _logger.LogInformation("File forcefully deleted: {fileName}, {fileSize}MiB", file.Name, ByteSize.FromBytes(file.Length).MebiBytes);
                deleteCurrentFile = true;
            }

            // only used if file in db has no raw size for whatever reason
            if (!deleteCurrentFile && file != null && fileCache.RawSize == 0)
            {
                try
                {
                    var length = LZ4Wrapper.Unwrap(File.ReadAllBytes(file.FullName)).LongLength;
                    _logger.LogInformation("Setting Raw File Size of " + fileCache.Hash + " to " + length);
                    fileCache.RawSize = length;
                    if (fileCounter % 1000 == 0)
                        await dbContext.SaveChangesAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not unpack {fileName}", file.FullName);
                }
            }

            // do actual deletion of file and remove also from db if needed
            if (deleteCurrentFile)
            {
                if (file != null) file.Delete();

                removedFileHashes.Add(fileCache.Hash);

                dbContext.Files.Remove(fileCache);
            }

            // only used if file in db has no size for whatever reason
            if (!deleteCurrentFile && file != null && fileCache.Size == 0)
            {
                _logger.LogInformation("Setting File Size of " + fileCache.Hash + " to " + file.Length);
                fileCache.Size = file.Length;
                // commit every 1000 files to db
                if (fileCounter % 1000 == 0)
                    await dbContext.SaveChangesAsync().ConfigureAwait(false);
            }

            fileCounter++;

            ct.ThrowIfCancellationRequested();
        }

        return removedFileHashes;
    }

    private List<string> CleanupViaFiles(List<FileInfo> allFilesInDir, int forcedDeletionAfterHours,
        DateTime lastAccessCutoffTime, DateTime forcedDeletionCutoffTime, CancellationToken ct)
    {
        List<string> removedFileHashes = new List<string>();

        foreach (var file in allFilesInDir)
        {
            bool deleteCurrentFile = false;
            if (file != null && file.LastAccessTime < lastAccessCutoffTime)
            {
                _logger.LogInformation("File outdated: {fileName}, {fileSize}MiB", file.Name, ByteSize.FromBytes(file.Length).MebiBytes);
                deleteCurrentFile = true;
            }
            else if (file != null && forcedDeletionAfterHours > 0 && file.LastWriteTime < forcedDeletionCutoffTime)
            {
                _logger.LogInformation("File forcefully deleted: {fileName}, {fileSize}MiB", file.Name, ByteSize.FromBytes(file.Length).MebiBytes);
                deleteCurrentFile = true;
            }

            if (deleteCurrentFile)
            {
                if (file != null) file.Delete();

                removedFileHashes.Add(file.Name);
            }

            ct.ThrowIfCancellationRequested();
        }

        return removedFileHashes;
    }

    private void InitializeGauges()
    {
        bool useColdStorage = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false);

        if (useColdStorage)
        {
            var coldStorageDir = _configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.ColdStorageDirectory));

            DirectoryInfo dirColdStorage = new(coldStorageDir);
            var allFilesInColdStorageDir = dirColdStorage.GetFiles("*", SearchOption.AllDirectories).ToList();

            _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotalSizeColdStorage, allFilesInColdStorageDir.Sum(f => f.Length));
            _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotalColdStorage, allFilesInColdStorageDir.Count);
        }

        var hotStorageDir = _configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));
        DirectoryInfo dirHotStorage = new(hotStorageDir);
        var allFilesInHotStorage = dirHotStorage.GetFiles("*", SearchOption.AllDirectories).ToList();

        _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotalSize, allFilesInHotStorage.Sum(f => { try { return f.Length; } catch { return 0; } }));
        _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotal, allFilesInHotStorage.Count);
    }
}