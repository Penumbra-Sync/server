using MareSynchronos.API;
using MareSynchronosShared.Utils;
using MareSynchronosStaticFilesServer.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route(MareFiles.Request)]
public class RequestController : ControllerBase
{
    private readonly CachedFileProvider _cachedFileProvider;
    private readonly RequestQueueService _requestQueue;
    private static SemaphoreSlim _parallelRequestSemaphore = new(250);

    public RequestController(ILogger<RequestController> logger, CachedFileProvider cachedFileProvider, RequestQueueService requestQueue,
        ServerTokenGenerator generator) : base(logger, generator)
    {
        _cachedFileProvider = cachedFileProvider;
        _requestQueue = requestQueue;
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
            var queueStatus = await _requestQueue.EnqueueUser(new(g, MareUser, file));
            return Ok(JsonSerializer.Serialize(new QueueRequestDto(g, queueStatus)));
        }
        catch (OperationCanceledException) { return BadRequest(); }
        finally
        {
            _parallelRequestSemaphore.Release();
        }
    }

    [HttpGet]
    [Route(MareFiles.Request_CheckQueue)]
    public async Task<IActionResult> CheckQueueAsync(Guid requestId)
    {
        try
        {
            await _parallelRequestSemaphore.WaitAsync(HttpContext.RequestAborted);
            if (_requestQueue.IsActiveProcessing(requestId, MareUser, out _))
            {
                return Ok();
            }

            if (_requestQueue.StillEnqueued(requestId, MareUser, out int position))
            {
                return Conflict(position);
            }

            return BadRequest();
        }
        catch (OperationCanceledException) { return BadRequest(); }
        finally
        {
            _parallelRequestSemaphore.Release();
        }
    }
}
