using MareSynchronos.API;
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

    [HttpPost]
    [Route(MareFiles.Request_Enqueue)]
    public IActionResult PreRequestFiles([FromBody] List<string> files)
    {
        foreach (var file in files)
        {
            _cachedFileProvider.DownloadFileWhenRequired(file, Authorization);
        }

        return Ok();
    }

    [HttpGet]
    [Route(MareFiles.Request_RequestFile)]
    public IActionResult RequestFile(string file)
    {
        Guid g = Guid.NewGuid();
        _cachedFileProvider.DownloadFileWhenRequired(file, Authorization);
        _requestQueue.EnqueueUser(new(g, User, file));
        return Ok(g.ToString());
    }

    [HttpGet]
    [Route(MareFiles.Request_CheckQueue)]
    public IActionResult CheckQueue(Guid requestId)
    {
        if (_requestQueue.IsActiveProcessing(requestId, User, out _)) return Ok();

        if (_requestQueue.StillEnqueued(requestId, User)) return Conflict();

        return BadRequest();
    }
}
