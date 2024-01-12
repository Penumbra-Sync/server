using MareSynchronosShared;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using MaxMind.GeoIP2;

namespace MareSynchronosServer.Services;

public class GeoIPService : IHostedService
{
    private readonly ILogger<GeoIPService> _logger;
    private readonly IConfigurationService<ServerConfiguration> _mareConfiguration;
    private bool _useGeoIP = false;
    private string _countryFile = string.Empty;
    private DatabaseReader? _dbReader;
    private DateTime _dbLastWriteTime = DateTime.Now;
    private CancellationTokenSource _fileWriteTimeCheckCts = new();
    private bool _processingReload = false;

    public GeoIPService(ILogger<GeoIPService> logger,
        IConfigurationService<ServerConfiguration> mareConfiguration)
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

            if (_dbReader.TryCountry(ip, out var response))
            {
                return response.Continent.Code;
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

                var useGeoIP = _mareConfiguration.GetValueOrDefault(nameof(ServerConfiguration.UseGeoIP), false);
                var countryFile = _mareConfiguration.GetValueOrDefault(nameof(ServerConfiguration.GeoIPDbCountryFile), string.Empty);
                var lastWriteTime = new FileInfo(countryFile).LastWriteTimeUtc;
                if (useGeoIP && (!string.Equals(countryFile, _countryFile, StringComparison.OrdinalIgnoreCase) || lastWriteTime != _dbLastWriteTime))
                {
                    _countryFile = countryFile;
                    if (!File.Exists(_countryFile)) throw new FileNotFoundException($"Could not open GeoIP Country Database, path does not exist: {_countryFile}");
                    _dbReader?.Dispose();
                    _dbReader = null;
                    _dbReader = new DatabaseReader(_countryFile);
                    _dbLastWriteTime = lastWriteTime;

                    _ = _dbReader.Country("8.8.8.8").Continent;

                    _logger.LogInformation($"Loaded GeoIP country file from {_countryFile}");

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
        _dbReader.Dispose();
        return Task.CompletedTask;
    }
}
