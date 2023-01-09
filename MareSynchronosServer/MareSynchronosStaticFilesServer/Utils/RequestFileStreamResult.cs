using MareSynchronosShared.Metrics;
using MareSynchronosStaticFilesServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Utils;

public class RequestFileStreamResult : FileStreamResult
{
    private readonly Guid _requestId;
    private readonly RequestQueueService _requestQueueService;
    private readonly MareMetrics _mareMetrics;
    private readonly CancellationTokenSource _releaseCts = new();
    private bool _releasedSlot = false;

    public RequestFileStreamResult(Guid requestId, int secondsUntilRelease, RequestQueueService requestQueueService,
        MareMetrics mareMetrics, Stream fileStream, string contentType) : base(fileStream, contentType)
    {
        _requestId = requestId;
        _requestQueueService = requestQueueService;
        _mareMetrics = mareMetrics;
        _mareMetrics.IncGauge(MetricsAPI.GaugeCurrentDownloads);

        // forcefully release slot after secondsUntilRelease
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(secondsUntilRelease), _releaseCts.Token).ConfigureAwait(false);
                _requestQueueService.FinishRequest(_requestId);
                _releasedSlot = true;
            }
            catch { }
        });
    }

    public override void ExecuteResult(ActionContext context)
    {
        base.ExecuteResult(context);

        _releaseCts.Cancel();

        if (!_releasedSlot)
            _requestQueueService.FinishRequest(_requestId);

        _mareMetrics.DecGauge(MetricsAPI.GaugeCurrentDownloads);
    }

    public override async Task ExecuteResultAsync(ActionContext context)
    {
        await base.ExecuteResultAsync(context).ConfigureAwait(false);

        _releaseCts.Cancel();

        if (!_releasedSlot)
            _requestQueueService.FinishRequest(_requestId);

        _mareMetrics.DecGauge(MetricsAPI.GaugeCurrentDownloads);
    }
}