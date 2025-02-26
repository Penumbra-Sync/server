using MareSynchronosShared.Metrics;
using System.Collections.Concurrent;

namespace MareSynchronosStaticFilesServer.Services;

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
            _metrics.IncGauge(MetricsAPI.GaugeFilesUniquePastHour);
            _metrics.IncGauge(MetricsAPI.GaugeFilesUniquePastHourSize, length);
        }
        if (!_pastDayFiles.ContainsKey(fileHash))
        {
            _pastDayFiles[fileHash] = length;
            _metrics.IncGauge(MetricsAPI.GaugeFilesUniquePastDay);
            _metrics.IncGauge(MetricsAPI.GaugeFilesUniquePastDaySize, length);
        }
    }

    public void LogRequest(long requestSize)
    {
        _metrics.IncCounter(MetricsAPI.CounterFileRequests, 1);
        _metrics.IncCounter(MetricsAPI.CounterFileRequestSize, requestSize);
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

            var now = DateTime.UtcNow;
            TimeOnly currentTime = new(now.Hour, now.Minute, now.Second);
            TimeOnly futureTime = new(now.Hour, 0, 0);
            var span = futureTime.AddHours(1) - currentTime;

            await Task.Delay(span, _resetCancellationTokenSource.Token).ConfigureAwait(false);
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

            var now = DateTime.UtcNow;
            DateTime midnight = new(new DateOnly(now.Date.Year, now.Date.Month, now.Date.Day), new(0, 0, 0));
            var span = midnight.AddDays(1) - now;

            await Task.Delay(span, _resetCancellationTokenSource.Token).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _resetCancellationTokenSource.Cancel();
        _logger.LogInformation("Stopping FileStatisticsService");
        return Task.CompletedTask;
    }
}
