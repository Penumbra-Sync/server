using MareSynchronosStaticFilesServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route("/cache")]
[ServiceFilter(typeof(DownloadActionFilter))]
public class ShardedFileController : ControllerBase
{
    private readonly CachedFileProvider _cachedFileProvider;
    private readonly RequestQueueService _requestQueue;

    public ShardedFileController(ILogger<ShardedFileController> logger, CachedFileProvider cachedFileProvider, RequestQueueService requestQueue) : base(logger)
    {
        _cachedFileProvider = cachedFileProvider;
        _requestQueue = requestQueue;
    }

    [HttpGet("get")]
    public async Task<IActionResult> GetFile()
    {
        var guid = Guid.Parse(Request.Headers["RequestId"]);
        if (!_requestQueue.IsActiveProcessing(guid, out var request)) return BadRequest();

        _logger.LogInformation($"GetFile:{User}:{guid}");

        var fs = await _cachedFileProvider.GetFileStream(request.FileId, Authorization);
        if (fs == null) return NotFound();

        return File(fs, "application/octet-stream");
    }
}
