using MareSynchronos.API.Routes;
using MareSynchronosShared;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route(MareFiles.Speedtest)]
public class SpeedTestController : ControllerBase
{
    private readonly IMemoryCache _memoryCache;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configurationService;
    private const string RandomByteDataName = "SpeedTestRandomByteData";
    private static readonly SemaphoreSlim _speedtestSemaphore = new(10, 10);

    public SpeedTestController(ILogger<SpeedTestController> logger,
        IMemoryCache memoryCache, IHttpContextAccessor httpContextAccessor,
        IConfigurationService<StaticFilesServerConfiguration> configurationService) : base(logger)
    {
        _memoryCache = memoryCache;
        _httpContextAccessor = httpContextAccessor;
        _configurationService = configurationService;
    }

    [HttpGet(MareFiles.Speedtest_Run)]
    public async Task<IActionResult> DownloadTest(CancellationToken cancellationToken)
    {
        var ip = _httpContextAccessor.GetIpAddress();
        var speedtestLimit = _configurationService.GetValueOrDefault(nameof(StaticFilesServerConfiguration.SpeedTestHoursRateLimit), 6);
        if (_memoryCache.TryGetValue<DateTime>(ip, out var value))
        {
            var hoursRemaining = value.Subtract(DateTime.UtcNow).TotalHours;
            return StatusCode(429, $"Can perform speedtest every {speedtestLimit} hours. {hoursRemaining:F2} hours remain.");
        }

        await _speedtestSemaphore.WaitAsync(cancellationToken);

        try
        {
            var expiry = DateTime.UtcNow.Add(TimeSpan.FromHours(speedtestLimit));
            _memoryCache.Set(ip, expiry, TimeSpan.FromHours(speedtestLimit));

            var randomByteData = _memoryCache.GetOrCreate(RandomByteDataName, (entry) =>
            {
                byte[] data = new byte[10 * 1024 * 1024];
                new Random().NextBytes(data);
                return data;
            });

            return File(randomByteData, "application/octet-stream", "speedtest.dat");
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, "Cancelled");
        }
        finally
        {
            _speedtestSemaphore.Release();
        }
    }
}
