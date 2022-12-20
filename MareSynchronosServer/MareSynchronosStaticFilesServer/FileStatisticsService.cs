using MareSynchronosShared.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MareSynchronosStaticFilesServer;

public class FileStatisticsService : IHostedService
{
    private readonly MareMetrics _metrics;
    private readonly ILogger<FileStatisticsService> _logger;
    private CancellationTokenSource _resetCancellationTokenSource;
    private ConcurrentDictionary<string, long> _pastHourFiles = new(StringComparer.Ordinal);
    private ConcurrentDictionary<string, long> _pastDayFiles = new(StringComparer.Ordinal);

    public FileStatisticsService(MareMetrics metrics, ILogger<FileStatisticsService> logger)
    {
        _metrics = metrics;
        _logger = logger;
    }

    public void LogFile(string fileHash, long length)
    {
        if (!_pastHourFiles.ContainsKey(fileHash))
        {
            _pastHourFiles[fileHash] = length;
            _metrics.SetGaugeTo(MetricsAPI.GaugeFilesUniquePastHour, _pastHourFiles.Count);
            _metrics.SetGaugeTo(MetricsAPI.GaugeFilesUniquePastHourSize, _pastHourFiles.Sum(f => f.Value));
        }
        if (!_pastDayFiles.ContainsKey(fileHash))
        {
            _pastDayFiles[fileHash] = length;
            _metrics.SetGaugeTo(MetricsAPI.GaugeFilesUniquePastDay, _pastDayFiles.Count);
            _metrics.SetGaugeTo(MetricsAPI.GaugeFilesUniquePastDaySize, _pastDayFiles.Sum(f => f.Value));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting FileStatisticsService");
        _resetCancellationTokenSource = new();
        _ = ResetHourlyFileData();
        _ = ResetDailyFileData();
        return Task.CompletedTask;
    }

    public async Task ResetHourlyFileData()
    {
        while (!_resetCancellationTokenSource.Token.IsCancellationRequested)
        {
            _logger.LogInformation("Resetting 1h Data");

            _pastHourFiles = new(StringComparer.Ordinal);
            _metrics.SetGaugeTo(MetricsAPI.GaugeFilesUniquePastHour, 0);
            _metrics.SetGaugeTo(MetricsAPI.GaugeFilesUniquePastHourSize, 0);
            await Task.Delay(TimeSpan.FromHours(1), _resetCancellationTokenSource.Token).ConfigureAwait(false);
        }
    }

    public async Task ResetDailyFileData()
    {
        while (!_resetCancellationTokenSource.Token.IsCancellationRequested)
        {
            _logger.LogInformation("Resetting 24h Data");

            _pastDayFiles = new(StringComparer.Ordinal);
            _metrics.SetGaugeTo(MetricsAPI.GaugeFilesUniquePastDay, 0);
            _metrics.SetGaugeTo(MetricsAPI.GaugeFilesUniquePastDaySize, 0);
            await Task.Delay(TimeSpan.FromDays(1), _resetCancellationTokenSource.Token).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _resetCancellationTokenSource.Cancel();
        _logger.LogInformation("Stopping FileStatisticsService");
        return Task.CompletedTask;
    }
}
