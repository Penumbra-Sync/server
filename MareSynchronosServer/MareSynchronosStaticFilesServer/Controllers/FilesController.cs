using MareSynchronosStaticFilesServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route("/cache")]
public class FilesController : ControllerBase
{
    private readonly CachedFileProvider _cachedFileProvider;

    public FilesController(ILogger<FilesController> logger, CachedFileProvider cachedFileProvider) : base(logger)
    {
        _cachedFileProvider = cachedFileProvider;
    }

    [HttpGet("{fileId}")]
    public async Task<IActionResult> GetFile(string fileId)
    {
        _logger.LogInformation($"GetFile:{User}:{fileId}");

        var fs = await _cachedFileProvider.GetFileStream(fileId, Authorization);
        if (fs == null) return NotFound();

        return File(fs, "application/octet-stream");
    }
}
