using MareSynchronosShared.Utils;
using MareSynchronosStaticFilesServer.Services;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MareSynchronosStaticFilesServer;

public class DownloadActionFilter : IResultFilter
{
    private readonly ILogger<DownloadActionFilter> _logger;
    private readonly RequestQueueService _requestQueue;

    public DownloadActionFilter(ILogger<DownloadActionFilter> logger, RequestQueueService requestQueue)
    {
        _logger = logger;
        _requestQueue = requestQueue;
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
        var request = Guid.Parse(context.HttpContext.Request.Headers["RequestId"]);
        _logger.LogDebug("Req Finished: {user} => {route}", request, context.HttpContext.Request.Path.Value);
        _requestQueue.FinishRequest(request);
    }

    public void OnResultExecuting(ResultExecutingContext context)
    {
        var request = Guid.Parse(context.HttpContext.Request.Headers["RequestId"]);
        _logger.LogDebug("Req Started: {user} => {route}", request, context.HttpContext.Request.Path.Value);
        _requestQueue.ActivateRequest(request);
    }
}