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
    public IActionResult RequestFile(string file)
    {
        Guid g = Guid.NewGuid();
        _requestQueue.EnqueueUser(new(g, User, file));
        return Ok(g);
    }

    [HttpGet]
    [Route("status")]
    public IActionResult CheckQueue(Guid file)
    {
        if (_requestQueue.IsActiveProcessing(file, out _))
        {
            return Ok();
        }

        return Conflict();
    }
}
