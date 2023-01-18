using MareSynchronos.API;
using MareSynchronosShared.Utils;
using MareSynchronosStaticFilesServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route(MareFiles.ServerFiles)]
public class ServerFilesController : ControllerBase
{
    private readonly CachedFileProvider _cachedFileProvider;

    public ServerFilesController(ILogger<ServerFilesController> logger, CachedFileProvider cachedFileProvider) : base(logger)
    {
        _cachedFileProvider = cachedFileProvider;
    }

    [HttpGet(MareFiles.ServerFiles_Get + "/{fileId}")]
    [Authorize(Policy = "Internal")]
    public IActionResult GetFile(string fileId)
    {
        _logger.LogInformation($"GetFile:{MareUser}:{fileId}");

        var fs = _cachedFileProvider.GetLocalFileStream(fileId);
        if (fs == null) return NotFound();

        return File(fs, "application/octet-stream");
    }
}
