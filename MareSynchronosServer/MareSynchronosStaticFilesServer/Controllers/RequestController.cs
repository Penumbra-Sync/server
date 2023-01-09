using MareSynchronosStaticFilesServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route("/request")]
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
    [Route("enqueue")]
    public IActionResult PreRequestFiles(List<string> files)
    {
        foreach (var file in files)
        {
            _cachedFileProvider.DownloadFileWhenRequired(file, Authorization);
        }

        return Ok();
    }

    [HttpGet]
    [Route("file")]
    public IActionResult RequestFile(string file)
    {
        Guid g = Guid.NewGuid();
        _cachedFileProvider.DownloadFileWhenRequired(file, Authorization);
        _requestQueue.EnqueueUser(new(g, User, file));
        return Ok(g.ToString());
    }

    [HttpGet]
    [Route("status")]
    public IActionResult CheckQueue(Guid requestId)
    {
        if (_requestQueue.IsActiveProcessing(requestId, User, out _))
        {
            return Ok();
        }

        return Conflict();
    }
}
