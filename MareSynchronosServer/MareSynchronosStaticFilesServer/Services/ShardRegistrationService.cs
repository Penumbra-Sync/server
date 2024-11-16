using MareSynchronos.API.Routes;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Utils.Configuration;

namespace MareSynchronosStaticFilesServer.Services;

public class ShardRegistrationService : IHostedService
{
    private readonly ILogger<ShardRegistrationService> _logger;
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configurationService;
    private readonly HttpClient _httpClient = new();
    private readonly CancellationTokenSource _heartBeatCts = new();
    private bool _isRegistered = false;

    public ShardRegistrationService(ILogger<ShardRegistrationService> logger,
        IConfigurationService<StaticFilesServerConfiguration> configurationService,
        ServerTokenGenerator serverTokenGenerator)
    {
        _logger = logger;
        _configurationService = configurationService;
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serverTokenGenerator.Token);
    }

    private void OnConfigChanged(object sender, EventArgs e)
    {
        _isRegistered = false;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting");
        _configurationService.ConfigChangedEvent += OnConfigChanged;
        _ = Task.Run(() => HeartbeatLoop(_heartBeatCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping");

        _configurationService.ConfigChangedEvent -= OnConfigChanged;
        _heartBeatCts.Cancel();
        _heartBeatCts.Dispose();
        // call unregister
        await UnregisterShard().ConfigureAwait(false);
        _httpClient.Dispose();
    }

    private async Task HeartbeatLoop(CancellationToken ct)
    {
        while (!_heartBeatCts.IsCancellationRequested)
        {
            try
            {
                await ProcessHeartbeat(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Issue during Heartbeat");
                _isRegistered = false;
            }

            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessHeartbeat(CancellationToken ct)
    {
        if (!_isRegistered)
        {
            await TryRegisterShard(ct).ConfigureAwait(false);
        }

        await ShardHeartbeat(ct).ConfigureAwait(false);
    }

    private async Task ShardHeartbeat(CancellationToken ct)
    {
        Uri mainServer = _configurationService.GetValue<Uri>(nameof(StaticFilesServerConfiguration.MainFileServerAddress));
        _logger.LogInformation("Running heartbeat against Main {server}", mainServer);

        using var heartBeat = await _httpClient.PostAsync(new Uri(mainServer, MareFiles.Main + "/shardHeartbeat"), null, ct).ConfigureAwait(false);
        heartBeat.EnsureSuccessStatusCode();
    }

    private async Task TryRegisterShard(CancellationToken ct)
    {
        Uri mainServer = _configurationService.GetValue<Uri>(nameof(StaticFilesServerConfiguration.MainFileServerAddress));
        _logger.LogInformation("Registering Shard with Main {server}", mainServer);
        var config = _configurationService.GetValue<ShardConfiguration>(nameof(StaticFilesServerConfiguration.ShardConfiguration));
        _logger.LogInformation("Config Value {varName}: {value}", nameof(ShardConfiguration.Continents), string.Join(", ", config.Continents));
        _logger.LogInformation("Config Value {varName}: {value}", nameof(ShardConfiguration.FileMatch), config.FileMatch);
        _logger.LogInformation("Config Value {varName}: {value}", nameof(ShardConfiguration.RegionUris), string.Join("; ", config.RegionUris.Select(k => k.Key + ":" + k.Value)));

        using var register = await _httpClient.PostAsJsonAsync(new Uri(mainServer, MareFiles.Main + "/shardRegister"), config, ct).ConfigureAwait(false);
        register.EnsureSuccessStatusCode();
        _isRegistered = true;
    }

    private async Task UnregisterShard()
    {
        Uri mainServer = _configurationService.GetValue<Uri>(nameof(StaticFilesServerConfiguration.MainFileServerAddress));
        _logger.LogInformation("Unregistering Shard with Main {server}", mainServer);
        using var heartBeat = await _httpClient.PostAsync(new Uri(mainServer, MareFiles.Main + "/shardUnregister"), null).ConfigureAwait(false);
    }
}
