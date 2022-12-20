using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Linq;
using System.Security.Claims;

namespace MareSynchronosStaticFilesServer;

[Route("/cache")]
public class FilesController : Controller
{
    private readonly ILogger<FilesController> _logger;
    private readonly IConfiguration _configuration;
    private readonly FileStatisticsService _fileStatisticsService;

    public FilesController(ILogger<FilesController> logger, IConfiguration configuration, FileStatisticsService fileStatisticsService)
    {
        _logger = logger;
        _configuration = configuration;
        _fileStatisticsService = fileStatisticsService;
    }

    [HttpGet("{fileId}")]
    public IActionResult GetFile(string fileId)
    {
        var authedUser = HttpContext.User.Claims.FirstOrDefault(f => string.Equals(f.Type, ClaimTypes.NameIdentifier, System.StringComparison.Ordinal))?.Value ?? "Unknown";
        _logger.LogInformation($"GetFile:{authedUser}:{fileId}");

        FileInfo fi = new(Path.Combine(_configuration.GetRequiredSection("MareSynchronos")["CacheDirectory"], fileId));
        if (!fi.Exists) return NotFound();

        _fileStatisticsService.LogFile(fileId, fi.Length);

        var fileStream = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);

        return File(fileStream, "application/octet-stream");
    }
}
