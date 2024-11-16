using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace MareSynchronosStaticFilesServer.Services;

public class MainServerShardRegistrationService : IHostedService
{
    private readonly ILogger<MainServerShardRegistrationService> _logger;
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configurationService;
    private readonly ConcurrentDictionary<string, ShardConfiguration> _shardConfigs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _shardHeartbeats = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _periodicCheckCts = new();

    public MainServerShardRegistrationService(ILogger<MainServerShardRegistrationService> logger,
        IConfigurationService<StaticFilesServerConfiguration> configurationService)
    {
        _logger = logger;
        _configurationService = configurationService;
    }

    public void RegisterShard(string shardName, ShardConfiguration shardConfiguration)
    {
        if (shardConfiguration == null || shardConfiguration == default)
            throw new InvalidOperationException("Empty configuration provided");

        if (_shardConfigs.ContainsKey(shardName))
            _logger.LogInformation("Re-Registering Shard {name}", shardName);
        else
            _logger.LogInformation("Registering Shard {name}", shardName);

        _shardHeartbeats[shardName] = DateTime.UtcNow;
        _shardConfigs[shardName] = shardConfiguration;
    }

    public void UnregisterShard(string shardName)
    {
        _logger.LogInformation("Unregistering Shard {name}", shardName);

        _shardHeartbeats.TryRemove(shardName, out _);
        _shardConfigs.TryRemove(shardName, out _);
    }

    public List<ShardConfiguration> GetConfigurationsByContinent(string continent)
    {
        var shardConfigs = _shardConfigs.Values.Where(v => v.Continents.Contains(continent, StringComparer.OrdinalIgnoreCase)).ToList();
        if (shardConfigs.Any()) return shardConfigs;
        shardConfigs = _shardConfigs.Values.Where(v => v.Continents.Contains("*", StringComparer.OrdinalIgnoreCase)).ToList();
        if (shardConfigs.Any()) return shardConfigs;
        return [new ShardConfiguration() {
            Continents = ["*"],
            FileMatch = ".*",
            RegionUris = new(StringComparer.Ordinal) {
                { "Central", _configurationService.GetValue<Uri>(nameof(StaticFilesServerConfiguration.CdnFullUrl)) }
            } }];
    }

    public void ShardHeartbeat(string shardName)
    {
        if (!_shardConfigs.ContainsKey(shardName))
            throw new InvalidOperationException("Shard not registered");

        _logger.LogInformation("Heartbeat from {name}", shardName);
        _shardHeartbeats[shardName] = DateTime.UtcNow;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => PeriodicHeartbeatCleanup(_periodicCheckCts.Token), cancellationToken).ConfigureAwait(false);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _periodicCheckCts.CancelAsync().ConfigureAwait(false);
        _periodicCheckCts.Dispose();
    }

    private async Task PeriodicHeartbeatCleanup(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var kvp in _shardHeartbeats.ToFrozenDictionary())
            {
                if (DateTime.UtcNow.Subtract(kvp.Value) > TimeSpan.FromMinutes(1))
                {
                    _shardHeartbeats.TryRemove(kvp.Key, out _);
                    _shardConfigs.TryRemove(kvp.Key, out _);
                }
            }

            await Task.Delay(5000, ct).ConfigureAwait(false);
        }
    }
}
