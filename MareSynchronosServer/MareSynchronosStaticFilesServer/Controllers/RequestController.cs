using MareSynchronos.API.Routes;
using MareSynchronosStaticFilesServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route(MareFiles.Request)]
public class RequestController : ControllerBase
{
    private readonly CachedFileProvider _cachedFileProvider;
    private readonly RequestQueueService _requestQueue;
    private static readonly SemaphoreSlim _parallelRequestSemaphore = new(500);

    public RequestController(ILogger<RequestController> logger, CachedFileProvider cachedFileProvider, RequestQueueService requestQueue) : base(logger)
    {
        _cachedFileProvider = cachedFileProvider;
        _requestQueue = requestQueue;
    }

    [HttpGet]
    [Route(MareFiles.Request_Cancel)]
    public async Task<IActionResult> CancelQueueRequest(Guid requestId)
    {
        await _parallelRequestSemaphore.WaitAsync(HttpContext.RequestAborted);

        try
        {
            _requestQueue.RemoveFromQueue(requestId, MareUser, IsPriority);
            return Ok();
        }
        catch (OperationCanceledException) { return BadRequest(); }
        finally
        {
            _parallelRequestSemaphore.Release();
        }
    }

    [HttpPost]
    [Route(MareFiles.Request_Enqueue)]
    public async Task<IActionResult> PreRequestFilesAsync([FromBody] IEnumerable<string> files)
    {
        await _parallelRequestSemaphore.WaitAsync(HttpContext.RequestAborted);

        try
        {
            foreach (var file in files)
            {
                _logger.LogDebug("Prerequested file: " + file);
                await _cachedFileProvider.DownloadFileWhenRequired(file).ConfigureAwait(false);
            }

            Guid g = Guid.NewGuid();
            _requestQueue.EnqueueUser(new(g, MareUser, files.ToList()), IsPriority);

            return Ok(g);
        }
        catch (OperationCanceledException) { return BadRequest(); }
        finally
        {
            _parallelRequestSemaphore.Release();
        }
    }

    [HttpGet]
    [Route(MareFiles.Request_Check)]
    public async Task<IActionResult> CheckQueueAsync(Guid requestId, [FromBody] IEnumerable<string> files)
    {
        await _parallelRequestSemaphore.WaitAsync(HttpContext.RequestAborted);

        try
        {
            if (!_requestQueue.StillEnqueued(requestId, MareUser, IsPriority))
                _requestQueue.EnqueueUser(new(requestId, MareUser, files.ToList()), IsPriority);
            return Ok();
        }
        catch (OperationCanceledException) { return BadRequest(); }
        finally
        {
            _parallelRequestSemaphore.Release();
        }
    }
}