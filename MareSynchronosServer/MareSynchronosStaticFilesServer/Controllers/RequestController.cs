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

    public RequestController(ILogger<RequestController> logger, CachedFileProvider cachedFileProvider, RequestQueueService requestQueue,
        ServerTokenGenerator generator) : base(logger, generator)
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
            _logger.LogDebug("Prerequested file: " + file);
            _cachedFileProvider.DownloadFileWhenRequired(file, Authorization);
        }

        return Ok();
    }

    [HttpGet]
    [Route(MareFiles.Request_RequestFile)]
    public async Task<IActionResult> RequestFile(string file)
    {
        Guid g = Guid.NewGuid();
        _cachedFileProvider.DownloadFileWhenRequired(file, Authorization);
        var queueStatus = await _requestQueue.EnqueueUser(new(g, MareUser, file));
        return Ok(JsonSerializer.Serialize(new QueueRequestDto(g, queueStatus)));
    }

    [HttpGet]
    [Route(MareFiles.Request_CheckQueue)]
    public IActionResult CheckQueue(Guid requestId)
    {
        if (_requestQueue.IsActiveProcessing(requestId, MareUser, out _)) return Ok();

        if (_requestQueue.StillEnqueued(requestId, MareUser, out int position)) return Conflict(position);

        return BadRequest();
    }
}
