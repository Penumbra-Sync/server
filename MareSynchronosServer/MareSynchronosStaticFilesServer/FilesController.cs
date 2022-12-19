using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace MareSynchronosStaticFilesServer
{
    [Route("/cache")]
    public class FilesController : Controller
    {
        private readonly ILogger<FilesController> logger;
        private readonly IConfiguration configuration;

        public FilesController(ILogger<FilesController> logger, IConfiguration configuration)
        {
            this.logger = logger;
            this.configuration = configuration;
        }

        [HttpGet("{fileId}")]
        public IActionResult GetFile(string fileId)
        {
            var authedUser = HttpContext.User.Claims.FirstOrDefault(f => string.Equals(f.Type, ClaimTypes.NameIdentifier, System.StringComparison.Ordinal))?.Value ?? "Unknown";
            logger.LogInformation($"GetFile:{authedUser}:{fileId}");

            FileInfo fi = new(Path.Combine(configuration.GetRequiredSection("MareSynchronos")["CacheDirectory"], fileId));
            if (!fi.Exists) return NotFound();

            var fileStream = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);

            return File(fileStream, "application/octet-stream");
        }
    }
}
