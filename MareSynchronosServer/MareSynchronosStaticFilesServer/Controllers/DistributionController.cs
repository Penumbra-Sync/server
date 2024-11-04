using MareSynchronos.API.Routes;
using MareSynchronosStaticFilesServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route(MareFiles.Distribution)]
public class DistributionController : ControllerBase
{
    private readonly CachedFileProvider _cachedFileProvider;

    public DistributionController(ILogger<DistributionController> logger, CachedFileProvider cachedFileProvider) : base(logger)
    {
        _cachedFileProvider = cachedFileProvider;
    }

    [HttpGet(MareFiles.Distribution_Get)]
    [Authorize(Policy = "Internal")]
    public async Task<IActionResult> GetFile(string fileHash)
    {
        _logger.LogInformation($"GetFile:{MareUser}:{fileHash}");

        var fs = await _cachedFileProvider.DownloadAndGetLocalFileInfo(fileHash);
        if (fs == null) return NotFound();

        return PhysicalFile(fs.FullName, "application/octet-stream");
    }
}
