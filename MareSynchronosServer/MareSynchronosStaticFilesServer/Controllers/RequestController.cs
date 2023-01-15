using MareSynchronos.API;
using MareSynchronosShared.Utils;
using MareSynchronosStaticFilesServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route(MareFiles.Request)]
public class RequestController : ControllerBase
{
    private readonly CachedFileProvider _cachedFileProvider;
    private readonly RequestQueueService _requestQueue;
    private static SemaphoreSlim _parallelRequestSemaphore = new(500);

    public RequestController(ILogger<RequestController> logger, CachedFileProvider cachedFileProvider, RequestQueueService requestQueue,
        ServerTokenGenerator generator) : base(logger, generator)
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
            await _parallelRequestSemaphore.WaitAsync(HttpContext.RequestAborted);
            _requestQueue.RemoveFromQueue(requestId, MareUser);
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
    public async Task<IActionResult> PreRequestFilesAsync([FromBody] List<string> files)
    {
        try
        {
            await _parallelRequestSemaphore.WaitAsync(HttpContext.RequestAborted);
            foreach (var file in files)
            {
                _logger.LogDebug("Prerequested file: " + file);
                _cachedFileProvider.DownloadFileWhenRequired(file, Authorization);
            }

            return Ok();
        }
        catch (OperationCanceledException) { return BadRequest(); }
        finally
        {
            _parallelRequestSemaphore.Release();
        }
    }

    [HttpGet]
    [Route(MareFiles.Request_RequestFile)]
    public async Task<IActionResult> RequestFile(string file)
    {
        try
        {
            await _parallelRequestSemaphore.WaitAsync(HttpContext.RequestAborted);
            Guid g = Guid.NewGuid();
            _cachedFileProvider.DownloadFileWhenRequired(file, Authorization);
            await _requestQueue.EnqueueUser(new(g, MareUser, file));
            return Ok(g);
        }
        catch (OperationCanceledException) { return BadRequest(); }
        finally
        {
            _parallelRequestSemaphore.Release();
        }
    }
}
