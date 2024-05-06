using MareSynchronosShared;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using MaxMind.GeoIP2;

namespace MareSynchronosAuthService.Services;

public class GeoIPService : IHostedService
{
    private readonly ILogger<GeoIPService> _logger;
    private readonly IConfigurationService<AuthServiceConfiguration> _mareConfiguration;
    private bool _useGeoIP = false;
    private string _cityFile = string.Empty;
    private DatabaseReader? _dbReader;
    private DateTime _dbLastWriteTime = DateTime.Now;
    private CancellationTokenSource _fileWriteTimeCheckCts = new();
    private bool _processingReload = false;

    public GeoIPService(ILogger<GeoIPService> logger,
        IConfigurationService<AuthServiceConfiguration> mareConfiguration)
    {
        _logger = logger;
        _mareConfiguration = mareConfiguration;
    }

    public async Task<string> GetCountryFromIP(IHttpContextAccessor httpContextAccessor)
    {
        if (!_useGeoIP)
        {
            return "*";
        }

        try
        {
            var ip = httpContextAccessor.GetIpAddress();

            using CancellationTokenSource waitCts = new();
            waitCts.CancelAfter(TimeSpan.FromSeconds(5));
            while (_processingReload) await Task.Delay(100, waitCts.Token).ConfigureAwait(false);

            if (_dbReader!.TryCity(ip, out var response))
            {
                string? continent = response?.Continent.Code;
                if (!string.IsNullOrEmpty(continent) &&
                    string.Equals(continent, "NA", StringComparison.Ordinal)
                    && response?.Location.Longitude != null)
                {
                    if (response.Location.Longitude < -102)
                    {
                        continent = "NA-W";
                    }
                    else
                    {
                        continent = "NA-E";
                    }
                }

                return continent ?? "*";
            }

            return "*";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling Geo IP country in request");
            return "*";
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("GeoIP module starting update task");

        var token = _fileWriteTimeCheckCts.Token;
        _ = PeriodicReloadTask(token);

        return Task.CompletedTask;
    }

    private async Task PeriodicReloadTask(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                _processingReload = true;

                var useGeoIP = _mareConfiguration.GetValueOrDefault(nameof(AuthServiceConfiguration.UseGeoIP), false);
                var cityFile = _mareConfiguration.GetValueOrDefault(nameof(AuthServiceConfiguration.GeoIPDbCityFile), string.Empty);
                var lastWriteTime = new FileInfo(cityFile).LastWriteTimeUtc;
                if (useGeoIP && (!string.Equals(cityFile, _cityFile, StringComparison.OrdinalIgnoreCase) || lastWriteTime != _dbLastWriteTime))
                {
                    _cityFile = cityFile;
                    if (!File.Exists(_cityFile)) throw new FileNotFoundException($"Could not open GeoIP City Database, path does not exist: {_cityFile}");
                    _dbReader?.Dispose();
                    _dbReader = null;
                    _dbReader = new DatabaseReader(_cityFile);
                    _dbLastWriteTime = lastWriteTime;

                    _ = _dbReader.City("8.8.8.8").Continent;

                    _logger.LogInformation($"Loaded GeoIP city file from {_cityFile}");

                    if (_useGeoIP != useGeoIP)
                    {
                        _logger.LogInformation("GeoIP module is now enabled");
                        _useGeoIP = useGeoIP;
                    }
                }

                if (_useGeoIP != useGeoIP && !useGeoIP)
                {
                    _logger.LogInformation("GeoIP module is now disabled");
                    _useGeoIP = useGeoIP;
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Error during periodic GeoIP module reload task, disabling GeoIP");
                _useGeoIP = false;
            }
            finally
            {
                _processingReload = false;
            }

            await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _fileWriteTimeCheckCts.Cancel();
        _fileWriteTimeCheckCts.Dispose();
        _dbReader?.Dispose();
        return Task.CompletedTask;
    }
}
