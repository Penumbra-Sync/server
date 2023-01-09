using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosStaticFilesServer.Utils;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace MareSynchronosStaticFilesServer.Services;

public class CachedFileProvider
{
    private readonly ILogger<CachedFileProvider> _logger;
    private readonly FileStatisticsService _fileStatisticsService;
    private readonly MareMetrics _metrics;
    private readonly Uri _remoteCacheSourceUri;
    private readonly string _basePath;
    private readonly ConcurrentDictionary<string, Task> _currentTransfers = new(StringComparer.Ordinal);
    private bool IsMainServer => _remoteCacheSourceUri == null;

    public CachedFileProvider(IConfigurationService<StaticFilesServerConfiguration> configuration, ILogger<CachedFileProvider> logger, FileStatisticsService fileStatisticsService, MareMetrics metrics)
    {
        _logger = logger;
        _fileStatisticsService = fileStatisticsService;
        _metrics = metrics;
        _remoteCacheSourceUri = configuration.GetValueOrDefault<Uri>(nameof(StaticFilesServerConfiguration.RemoteCacheSourceUri), null);
        _basePath = configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));
    }

    private async Task DownloadTask(string hash, string auth)
    {
        // download file from remote
        var downloadUrl = new Uri(_remoteCacheSourceUri, hash);
        _logger.LogInformation("Did not find {hash}, downloading from {server}", hash, downloadUrl);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization", auth);
        var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

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
        using var fileStream = new FileStream(fileName, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        var bufferSize = response.Content.Headers.ContentLength > 1024 * 1024 ? 4096 : 1024;
        var buffer = new byte[bufferSize];

        var bytesRead = 0;
        while ((bytesRead = await (await response.Content.ReadAsStreamAsync().ConfigureAwait(false)).ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
        }

        _metrics.IncGauge(MetricsAPI.GaugeFilesTotal);
        _metrics.IncGauge(MetricsAPI.GaugeFilesTotalSize, FilePathUtil.GetFileInfoForHash(_basePath, hash).Length);
    }

    public void DownloadFileWhenRequired(string hash, string auth)
    {
        var fi = FilePathUtil.GetFileInfoForHash(_basePath, hash);
        if (fi == null && IsMainServer) return;

        if (fi == null && !_currentTransfers.ContainsKey(hash))
        {
            _currentTransfers[hash] = DownloadTask(hash, auth).ContinueWith(r => _currentTransfers.Remove(hash, out _));
        }

        _fileStatisticsService.LogFile(hash, fi.Length);
    }

    public async Task<FileStream> GetFileStream(string hash, string auth)
    {
        var fi = FilePathUtil.GetFileInfoForHash(_basePath, hash);
        if (fi == null && IsMainServer) return null;

        if (fi == null && !_currentTransfers.ContainsKey(hash))
        {
            _currentTransfers[hash] = DownloadTask(hash, auth).ContinueWith(r => _currentTransfers.Remove(hash, out _));
        }

        if (_currentTransfers.TryGetValue(hash, out var downloadTask))
        {
            await downloadTask.ConfigureAwait(false);
        }

        fi = FilePathUtil.GetFileInfoForHash(_basePath, hash);
        if (fi == null) return null;

        _fileStatisticsService.LogFile(hash, fi.Length);

        return new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.Inheritable | FileShare.Read);
    }
}