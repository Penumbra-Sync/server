using MareSynchronos.API.Routes;
using MareSynchronosStaticFilesServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route(MareFiles.Request)]
public class RequestController : ControllerBase
{
    private readonly CachedFileProvider _cachedFileProvider;
    private readonly RequestQueueService _requestQueue;

    public RequestController(ILogger<RequestController> logger, CachedFileProvider cachedFileProvider, RequestQueueService requestQueue) : base(logger)
    {
        _cachedFileProvider = cachedFileProvider;
        _requestQueue = requestQueue;
    }

    [HttpGet]
    [Route(MareFiles.Request_Cancel)]
    public async Task<IActionResult> CancelQueueRequest(Guid requestId)
    {
        try
        {
            _requestQueue.RemoveFromQueue(requestId, MareUser, IsPriority);
            return Ok();
        }
        catch (OperationCanceledException) { return BadRequest(); }
    }

    [HttpPost]
    [Route(MareFiles.Request_Enqueue)]
    public async Task<IActionResult> PreRequestFilesAsync([FromBody] IEnumerable<string> files)
    {
        try
        {
            foreach (var file in files)
            {
                _logger.LogDebug("Prerequested file: " + file);
                await _cachedFileProvider.DownloadFileWhenRequired(file).ConfigureAwait(false);
            }

            Guid g = Guid.NewGuid();
            await _requestQueue.EnqueueUser(new(g, MareUser, files.ToList()), IsPriority, HttpContext.RequestAborted);

            return Ok(g);
        }
        catch (OperationCanceledException) { return BadRequest(); }
    }

    [HttpGet]
    [Route(MareFiles.Request_Check)]
    public async Task<IActionResult> CheckQueueAsync(Guid requestId, [FromBody] IEnumerable<string> files)
    {
        try
        {
            if (!_requestQueue.StillEnqueued(requestId, MareUser, IsPriority))
                await _requestQueue.EnqueueUser(new(requestId, MareUser, files.ToList()), IsPriority, HttpContext.RequestAborted);
            return Ok();
        }
        catch (OperationCanceledException) { return BadRequest(); }
    }
}