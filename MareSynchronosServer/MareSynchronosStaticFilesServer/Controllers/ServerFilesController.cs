using MareSynchronos.API;
using MareSynchronosShared.Services;
using MareSynchronosStaticFilesServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route(MareFiles.ServerFiles)]
public class ServerFilesController : ControllerBase
{
    private readonly CachedFileProvider _cachedFileProvider;

    public ServerFilesController(ILogger<ServerFilesController> logger, CachedFileProvider cachedFileProvider, IConfigurationService<StaticFilesServerConfiguration> configuration) : base(logger, configuration)
    {
        _cachedFileProvider = cachedFileProvider;
    }

    [HttpGet(MareFiles.ServerFiles_Get + "/{fileId}")]
    [Authorize(Policy = "Internal")]
    public async Task<IActionResult> GetFile(string fileId)
    {
        _logger.LogInformation($"GetFile:{User}:{fileId}");

        var fs = _cachedFileProvider.GetLocalFileStream(fileId);
        if (fs == null) return NotFound();

        return File(fs, "application/octet-stream");
    }
}
