using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
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

    public RequestFileStreamResult Create(Guid requestId, Stream stream)
    {
        return new RequestFileStreamResult(requestId, _requestQueueService,
            _metrics, stream, "application/octet-stream");
    }
}