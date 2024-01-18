using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosStaticFilesServer.Utils;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using MareSynchronosShared.Utils;
using MareSynchronos.API.Routes;

namespace MareSynchronosStaticFilesServer.Services;

public sealed class CachedFileProvider : IDisposable
{
    private readonly ILogger<CachedFileProvider> _logger;
    private readonly FileStatisticsService _fileStatisticsService;
    private readonly MareMetrics _metrics;
    private readonly ServerTokenGenerator _generator;
    private readonly Uri _remoteCacheSourceUri;
    private readonly string _basePath;
    private readonly ConcurrentDictionary<string, Task> _currentTransfers = new(StringComparer.Ordinal);
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _downloadSemaphore = new(1);
    private bool _disposed;

    private bool IsMainServer => _remoteCacheSourceUri == null && _isDistributionServer;
    private bool _isDistributionServer;

    public CachedFileProvider(IConfigurationService<StaticFilesServerConfiguration> configuration, ILogger<CachedFileProvider> logger, FileStatisticsService fileStatisticsService, MareMetrics metrics, ServerTokenGenerator generator)
    {
        _logger = logger;
        _fileStatisticsService = fileStatisticsService;
        _metrics = metrics;
        _generator = generator;
        _remoteCacheSourceUri = configuration.GetValueOrDefault<Uri>(nameof(StaticFilesServerConfiguration.DistributionFileServerAddress), null);
        _isDistributionServer = configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.IsDistributionNode), false);
        _basePath = configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));
        _httpClient = new();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MareSynchronosServer", "1.0.0.0"));
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
        // download file from remote
        var downloadUrl = MareFiles.DistributionGetFullPath(_remoteCacheSourceUri, hash);
        _logger.LogInformation("Did not find {hash}, downloading from {server}", hash, downloadUrl);

        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _generator.Token);
        using var response = await _httpClient.SendAsync(requestMessage).ConfigureAwait(false);

        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download {url}", downloadUrl);
            return;
        }

        var fileName = FilePathUtil.GetFilePath(_basePath, hash);
        var tempFileName = fileName + ".dl";
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
        File.Move(tempFileName, fileName, true);

        _metrics.IncGauge(MetricsAPI.GaugeFilesTotal);
        _metrics.IncGauge(MetricsAPI.GaugeFilesTotalSize, FilePathUtil.GetFileInfoForHash(_basePath, hash).Length);
    }

    public async Task DownloadFileWhenRequired(string hash)
    {
        var fi = FilePathUtil.GetFileInfoForHash(_basePath, hash);
        if (fi == null && IsMainServer) return;

        await _downloadSemaphore.WaitAsync().ConfigureAwait(false);
        if ((fi.Length == 0 || fi == null) && !_currentTransfers.ContainsKey(hash))
        {
            _currentTransfers[hash] = Task.Run(async () =>
            {
                try
                {
                    await DownloadTask(hash).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during Download Task for {hash}", hash);
                }
                finally
                {
                    _currentTransfers.Remove(hash, out _);
                }
            });
        }
        _downloadSemaphore.Release();
    }

    public FileStream? GetLocalFileStream(string hash)
    {
        var fi = FilePathUtil.GetFileInfoForHash(_basePath, hash);
        if (fi == null) return null;

        _fileStatisticsService.LogFile(hash, fi.Length);

        return new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.Inheritable | FileShare.Read);
    }

    public async Task<FileStream?> GetAndDownloadFileStream(string hash)
    {
        await DownloadFileWhenRequired(hash).ConfigureAwait(false);

        if (_currentTransfers.TryGetValue(hash, out var downloadTask))
        {
            await downloadTask.ConfigureAwait(false);
        }

        return GetLocalFileStream(hash);
    }
}