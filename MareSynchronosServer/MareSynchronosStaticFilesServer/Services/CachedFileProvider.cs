using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosStaticFilesServer.Utils;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using MareSynchronosShared.Utils;
using MareSynchronos.API.Routes;
using MareSynchronosShared.Utils.Configuration;

namespace MareSynchronosStaticFilesServer.Services;

public sealed class CachedFileProvider : IDisposable
{
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configuration;
    private readonly ILogger<CachedFileProvider> _logger;
    private readonly FileStatisticsService _fileStatisticsService;
    private readonly MareMetrics _metrics;
    private readonly ServerTokenGenerator _generator;
    private readonly Uri _remoteCacheSourceUri;
    private readonly string _hotStoragePath;
    private readonly ConcurrentDictionary<string, Task> _currentTransfers = new(StringComparer.Ordinal);
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _downloadSemaphore = new(1, 1);
    private bool _disposed;

    private bool IsMainServer => _remoteCacheSourceUri == null && _isDistributionServer;
    private bool _isDistributionServer;

    public CachedFileProvider(IConfigurationService<StaticFilesServerConfiguration> configuration, ILogger<CachedFileProvider> logger,
        FileStatisticsService fileStatisticsService, MareMetrics metrics, ServerTokenGenerator generator)
    {
        _configuration = configuration;
        _logger = logger;
        _fileStatisticsService = fileStatisticsService;
        _metrics = metrics;
        _generator = generator;
        _remoteCacheSourceUri = configuration.GetValueOrDefault<Uri>(nameof(StaticFilesServerConfiguration.DistributionFileServerAddress), null);
        _isDistributionServer = configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.IsDistributionNode), false);
        _hotStoragePath = configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));
        _httpClient = new();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MareSynchronosServer", "1.0.0.0"));
        _httpClient.Timeout = TimeSpan.FromSeconds(300);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient?.Dispose();
    }

    private async Task DownloadTask(string hash)
    {
        var destinationFilePath = FilePathUtil.GetFilePath(_hotStoragePath, hash);

        // first check cold storage
        if (TryCopyFromColdStorage(hash, destinationFilePath)) return;

        // if cold storage is not configured or file not found or error is present try to download file from remote
        var downloadUrl = MareFiles.DistributionGetFullPath(_remoteCacheSourceUri, hash);
        _logger.LogInformation("Did not find {hash}, downloading from {server}", hash, downloadUrl);

        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _generator.Token);
        HttpResponseMessage? response = null;

        try
        {
            response = await _httpClient.SendAsync(requestMessage).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download {url}", downloadUrl);
            response?.Dispose();
            return;
        }

        var tempFileName = destinationFilePath + ".dl";
        var fileStream = new FileStream(tempFileName, FileMode.Create, FileAccess.ReadWrite);
        var bufferSize = response.Content.Headers.ContentLength > 1024 * 1024 ? 4096 : 1024;
        var buffer = new byte[bufferSize];

        var bytesRead = 0;
        using var content = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        while ((bytesRead = await content.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
        }
        await fileStream.FlushAsync().ConfigureAwait(false);
        await fileStream.DisposeAsync().ConfigureAwait(false);
        File.Move(tempFileName, destinationFilePath, true);

        _metrics.IncGauge(MetricsAPI.GaugeFilesTotal);
        _metrics.IncGauge(MetricsAPI.GaugeFilesTotalSize, FilePathUtil.GetFileInfoForHash(_hotStoragePath, hash).Length);
        response.Dispose();
    }

    private bool TryCopyFromColdStorage(string hash, string destinationFilePath)
    {
        if (!_configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false)) return false;

        string coldStorageDir = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.ColdStorageDirectory), string.Empty);
        if (string.IsNullOrEmpty(coldStorageDir)) return false;

        var coldStorageFilePath = FilePathUtil.GetFileInfoForHash(coldStorageDir, hash);
        if (coldStorageFilePath == null) return false;

        try
        {
            _logger.LogDebug("Copying {hash} from cold storage: {path}", hash, coldStorageFilePath);
            var tempFileName = destinationFilePath + ".dl";
            File.Copy(coldStorageFilePath.FullName, tempFileName, true);
            File.Move(tempFileName, destinationFilePath, true);
            coldStorageFilePath.LastAccessTimeUtc = DateTime.UtcNow;
            var destinationFile = new FileInfo(destinationFilePath);
            destinationFile.LastAccessTimeUtc = DateTime.UtcNow;
            destinationFile.CreationTimeUtc = DateTime.UtcNow;
            destinationFile.LastWriteTimeUtc = DateTime.UtcNow;
            _metrics.IncGauge(MetricsAPI.GaugeFilesTotal);
            _metrics.IncGauge(MetricsAPI.GaugeFilesTotalSize, new FileInfo(destinationFilePath).Length);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not copy {coldStoragePath} from cold storage", coldStorageFilePath);
        }

        return false;
    }

    public async Task DownloadFileWhenRequired(string hash)
    {
        var fi = FilePathUtil.GetFileInfoForHash(_hotStoragePath, hash);
        if (fi == null && IsMainServer)
        {
            TryCopyFromColdStorage(hash, FilePathUtil.GetFilePath(_hotStoragePath, hash));
            return;
        }

        await _downloadSemaphore.WaitAsync().ConfigureAwait(false);
        if ((fi == null || (fi?.Length ?? 0) == 0)
            && (!_currentTransfers.TryGetValue(hash, out var downloadTask)
                || (downloadTask?.IsCompleted ?? true)))
        {
            _currentTransfers[hash] = Task.Run(async () =>
            {
                try
                {
                    _metrics.IncGauge(MetricsAPI.GaugeFilesDownloadingFromCache);
                    await DownloadTask(hash).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during Download Task for {hash}", hash);
                }
                finally
                {
                    _metrics.DecGauge(MetricsAPI.GaugeFilesDownloadingFromCache);
                    _currentTransfers.Remove(hash, out _);
                }
            });
        }
        _downloadSemaphore.Release();
    }

    public FileInfo? GetLocalFilePath(string hash)
    {
        var fi = FilePathUtil.GetFileInfoForHash(_hotStoragePath, hash);
        if (fi == null) return null;

        fi.LastAccessTimeUtc = DateTime.UtcNow;

        _fileStatisticsService.LogFile(hash, fi.Length);

        return new FileInfo(fi.FullName);
    }

    public async Task<FileInfo?> DownloadAndGetLocalFileInfo(string hash)
    {
        await DownloadFileWhenRequired(hash).ConfigureAwait(false);

        if (_currentTransfers.TryGetValue(hash, out var downloadTask))
        {
            try
            {
                using CancellationTokenSource cts = new();
                cts.CancelAfter(TimeSpan.FromSeconds(300));
                _metrics.IncGauge(MetricsAPI.GaugeFilesTasksWaitingForDownloadFromCache);
                await downloadTask.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed while waiting for download task for {hash}", hash);
                return null;
            }
            finally
            {
                _metrics.DecGauge(MetricsAPI.GaugeFilesTasksWaitingForDownloadFromCache);
            }
        }

        return GetLocalFilePath(hash);
    }

    public bool AnyFilesDownloading(List<string> hashes)
    {
        return hashes.Exists(_currentTransfers.Keys.Contains);
    }
}