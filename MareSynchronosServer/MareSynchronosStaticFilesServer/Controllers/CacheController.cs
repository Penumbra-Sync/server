using MareSynchronos.API.Routes;
using MareSynchronosStaticFilesServer.Services;
using MareSynchronosStaticFilesServer.Utils;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route(MareFiles.Cache)]
public class CacheController : ControllerBase
{
    private readonly RequestFileStreamResultFactory _requestFileStreamResultFactory;
    private readonly CachedFileProvider _cachedFileProvider;
    private readonly RequestQueueService _requestQueue;
    private readonly FileStatisticsService _fileStatisticsService;

    public CacheController(ILogger<CacheController> logger, RequestFileStreamResultFactory requestFileStreamResultFactory,
        CachedFileProvider cachedFileProvider, RequestQueueService requestQueue, FileStatisticsService fileStatisticsService) : base(logger)
    {
        _requestFileStreamResultFactory = requestFileStreamResultFactory;
        _cachedFileProvider = cachedFileProvider;
        _requestQueue = requestQueue;
        _fileStatisticsService = fileStatisticsService;
    }

    [HttpGet(MareFiles.Cache_Get)]
    public async Task<IActionResult> GetFiles(Guid requestId)
    {
        _logger.LogDebug($"GetFile:{MareUser}:{requestId}");

        if (!_requestQueue.IsActiveProcessing(requestId, MareUser, out var request)) return BadRequest();

        _requestQueue.ActivateRequest(requestId);

        Response.ContentType = "application/octet-stream";
        var memoryStream = new MemoryStream();
        var streamWriter = new BinaryWriter(memoryStream);

        long requestSize = 0;

        foreach (var file in request.FileIds)
        {
            var fs = await _cachedFileProvider.GetAndDownloadFileStream(file);
            if (fs == null) continue;
            streamWriter.Write(Encoding.ASCII.GetBytes("#" + file + ":" + fs.Length.ToString(CultureInfo.InvariantCulture) + "#"));
            byte[] buffer = new byte[fs.Length];
            _ = await fs.ReadAsync(buffer, HttpContext.RequestAborted);
            streamWriter.Write(buffer);
            requestSize += fs.Length;
        }

        streamWriter.Flush();
        memoryStream.Position = 0;

        _fileStatisticsService.LogRequest(requestSize);

        return _requestFileStreamResultFactory.Create(requestId, memoryStream);
    }
}
