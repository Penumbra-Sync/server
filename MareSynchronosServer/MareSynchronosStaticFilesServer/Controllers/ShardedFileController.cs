using MareSynchronos.API;
using MareSynchronosStaticFilesServer.Services;
using MareSynchronosStaticFilesServer.Utils;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route(MareFiles.Files)]
public class ShardedFileController : ControllerBase
{
    private readonly RequestFileStreamResultFactory _requestFileStreamResultFactory;
    private readonly CachedFileProvider _cachedFileProvider;
    private readonly RequestQueueService _requestQueue;

    public ShardedFileController(ILogger<ShardedFileController> logger, RequestFileStreamResultFactory requestFileStreamResultFactory,
        CachedFileProvider cachedFileProvider, RequestQueueService requestQueue) : base(logger)
    {
        _requestFileStreamResultFactory = requestFileStreamResultFactory;
        _cachedFileProvider = cachedFileProvider;
        _requestQueue = requestQueue;
    }

    [HttpGet(MareFiles.Files_Get)]
    public async Task<IActionResult> GetFile(Guid requestId)
    {
        _logger.LogInformation($"GetFile:{User}:{requestId}");

        if (!_requestQueue.IsActiveProcessing(requestId, User, out var request)) return BadRequest();

        _requestQueue.ActivateRequest(requestId);

        var fs = await _cachedFileProvider.GetFileStream(request.FileId, Authorization);
        if (fs == null)
        {
            _requestQueue.FinishRequest(requestId);
            return NotFound();
        }

        return _requestFileStreamResultFactory.Create(requestId, fs);
    }
}
