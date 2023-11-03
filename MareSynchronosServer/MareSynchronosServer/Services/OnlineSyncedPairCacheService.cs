using MareSynchronosShared.Metrics;
using System.Collections.Concurrent;

namespace MareSynchronosServer.Services;

public class OnlineSyncedPairCacheService : IHostedService
{
    private const int CleanupCount = 1;
    private const int CacheCount = 500;
    private Task? _cleanUpTask;
    private readonly CancellationTokenSource _runnerCts = new();
    private readonly ConcurrentDictionary<string, Dictionary<string, DateTime>> _lastSeenCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cleanupSemaphore = new(CleanupCount);
    private readonly SemaphoreSlim _cacheSemaphore = new(CacheCount);
    private readonly SemaphoreSlim _cacheAdditionSemaphore = new(1);
    private readonly ILogger<OnlineSyncedPairCacheService> _logger;
    private readonly MareMetrics _mareMetrics;

    public OnlineSyncedPairCacheService(ILogger<OnlineSyncedPairCacheService> logger, MareMetrics mareMetrics)
    {
        _logger = logger;
        _mareMetrics = mareMetrics;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cleanUpTask = CleanUp(_runnerCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _runnerCts.Cancel();
        await _cleanUpTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> AreAllPlayersCached(string sender, List<string> uids, CancellationToken ct)
    {
        while (_cleanupSemaphore.CurrentCount == 0)
            await Task.Delay(250, ct).ConfigureAwait(false);

        await _cacheSemaphore.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (ct.IsCancellationRequested)
                return false;
            if (!_lastSeenCache.TryGetValue(sender, out var senderCache))
                return false;

            lock (senderCache)
            {
                var cachedUIDs = senderCache.Keys.ToList();
                var allCached = uids.TrueForAll(u => cachedUIDs.Contains(u, StringComparer.OrdinalIgnoreCase));

                _logger.LogDebug("AreAllPlayersCached:{uid}:{count}:{result}", sender, uids.Count, allCached);

                if (allCached) _mareMetrics.IncCounter(MetricsAPI.CounterUserPairCacheHit);
                else _mareMetrics.IncCounter(MetricsAPI.CounterUserPairCacheMiss);

                return allCached;
            }
        }
        finally
        {
            _cacheSemaphore.Release();
        }
    }

    public async Task CachePlayers(string sender, List<string> uids, CancellationToken ct)
    {
        while (_cleanupSemaphore.CurrentCount == 0)
            await Task.Delay(250, ct).ConfigureAwait(false);

        await _cacheSemaphore.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (ct.IsCancellationRequested) return;
            if (!_lastSeenCache.TryGetValue(sender, out var senderCache))
            {
                await _cacheAdditionSemaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!_lastSeenCache.ContainsKey(sender))
                    {
                        _lastSeenCache[sender] = senderCache = new(StringComparer.Ordinal);
                        _mareMetrics.IncGauge(MetricsAPI.GaugeUserPairCacheEntries);
                    }
                }
                finally
                {
                    _cacheAdditionSemaphore.Release();
                }
            }

            lock (senderCache)
            {
                var lastSeen = DateTime.UtcNow.AddMinutes(60);
                _logger.LogDebug("CacheOnlinePlayers:{uid}:{count}", sender, uids.Count);
                var newEntries = uids.Count(u => !senderCache.ContainsKey(u));

                _mareMetrics.IncCounter(MetricsAPI.CounterUserPairCacheNewEntries, newEntries);
                _mareMetrics.IncCounter(MetricsAPI.CounterUserPairCacheUpdatedEntries, uids.Count - newEntries);

                _mareMetrics.IncGauge(MetricsAPI.GaugeUserPairCacheEntries, newEntries);
                uids.ForEach(u => senderCache[u] = lastSeen);
            }
        }
        finally
        {
            _cacheSemaphore.Release();
        }
    }

    private async Task CleanUp(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

            _logger.LogInformation("Cleaning up stale entries");

            try
            {
                await _cleanupSemaphore.WaitAsync(ct).ConfigureAwait(false);
                while (_cacheSemaphore.CurrentCount != CacheCount)
                    await Task.Delay(25, ct).ConfigureAwait(false);
                CleanUpCache(ct);
            }
            finally
            {
                _cleanupSemaphore.Release();
            }
        }
    }

    private void CleanUpCache(CancellationToken ct)
    {
        try
        {
            int entriesRemoved = 0;
            int playersRemoved = 0;
            foreach (var playerCache in _lastSeenCache.ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal))
            {
                foreach (var cacheEntry in playerCache.Value.ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal))
                {
                    if (cacheEntry.Value < DateTime.UtcNow)
                    {
                        entriesRemoved++;
                        playerCache.Value.Remove(cacheEntry.Key);
                    }
                }

                ct.ThrowIfCancellationRequested();

                if (!playerCache.Value.Any())
                {
                    playersRemoved++;
                    _lastSeenCache.Remove(playerCache.Key, out _);
                }
            }

            _logger.LogInformation("Cleaning up complete, removed {entries} individual entries and {players} players", entriesRemoved, playersRemoved);
            _mareMetrics.SetGaugeTo(MetricsAPI.GaugeUserPairCacheEntries, _lastSeenCache.Values.SelectMany(k => k.Keys).Count());
            _mareMetrics.SetGaugeTo(MetricsAPI.GaugeUserPairCacheUsers, _lastSeenCache.Keys.Count());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cleanup failed");
        }
    }
}
