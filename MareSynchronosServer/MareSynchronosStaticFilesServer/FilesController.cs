using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer;

[Route("/cache")]
public class FilesController : Controller
{
    private readonly ILogger<FilesController> _logger;
    private readonly CachedFileProvider _cachedFileProvider;

    public FilesController(ILogger<FilesController> logger, CachedFileProvider cachedFileProvider)
    {
        _logger = logger;
        _cachedFileProvider = cachedFileProvider;
    }

    [HttpGet("{fileId}")]
    public async Task<IActionResult> GetFile(string fileId)
    {
        var authedUser = HttpContext.User.Claims.FirstOrDefault(f => string.Equals(f.Type, MareClaimTypes.Uid, StringComparison.Ordinal))?.Value ?? "Unknown";
        _logger.LogInformation($"GetFile:{authedUser}:{fileId}");

        var fs = await _cachedFileProvider.GetFileStream(fileId, Request.Headers["Authorization"]);
        if (fs == null) return NotFound();

        return File(fs, "application/octet-stream");
    }
}
