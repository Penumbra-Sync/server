using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosStaticFilesServer.Services;

namespace MareSynchronosStaticFilesServer.Utils;

public class RequestFileStreamResultFactory
{
    private readonly MareMetrics _metrics;
    private readonly RequestQueueService _requestQueueService;
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configurationService;

    public RequestFileStreamResultFactory(MareMetrics metrics, RequestQueueService requestQueueService, IConfigurationService<StaticFilesServerConfiguration> configurationService)
    {
        _metrics = metrics;
        _requestQueueService = requestQueueService;
        _configurationService = configurationService;
    }

    public RequestFileStreamResult Create(Guid requestId, MemoryStream ms)
    {
        return new RequestFileStreamResult(requestId, _configurationService.GetValueOrDefault(nameof(StaticFilesServerConfiguration.DownloadQueueReleaseSeconds), 15),
            _requestQueueService, _metrics, ms, "application/octet-stream");
    }
}