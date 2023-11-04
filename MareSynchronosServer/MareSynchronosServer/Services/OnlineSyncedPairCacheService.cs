using MareSynchronosShared.Metrics;

namespace MareSynchronosServer.Services;

public class OnlineSyncedPairCacheService
{
    private readonly Dictionary<string, PairCache> _lastSeenCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cacheModificationSemaphore = new(1);
    private readonly ILogger<OnlineSyncedPairCacheService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly MareMetrics _mareMetrics;

    public OnlineSyncedPairCacheService(ILogger<OnlineSyncedPairCacheService> logger, ILoggerFactory loggerFactory, MareMetrics mareMetrics)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _mareMetrics = mareMetrics;
    }

    public async Task InitPlayer(string user)
    {
        if (_lastSeenCache.ContainsKey(user)) return;

        await _cacheModificationSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            _logger.LogDebug("Initializing {user}", user);
            _lastSeenCache[user] = new(_loggerFactory.CreateLogger<PairCache>(), user, _mareMetrics);
        }
        finally
        {
            _cacheModificationSemaphore.Release();
        }
    }

    public async Task DisposePlayer(string user)
    {
        if (!_lastSeenCache.ContainsKey(user)) return;

        await _cacheModificationSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            _logger.LogDebug("Disposing {user}", user);
            _lastSeenCache.Remove(user, out var pairCache);
            pairCache?.Dispose();
        }
        finally
        {
            _cacheModificationSemaphore.Release();
        }
    }

    public async Task<bool> AreAllPlayersCached(string sender, List<string> uids, CancellationToken ct)
    {
        if (!_lastSeenCache.ContainsKey(sender)) await InitPlayer(sender).ConfigureAwait(false);

        _lastSeenCache.TryGetValue(sender, out var pairCache);
        return await pairCache.AreAllPlayersCached(uids, ct).ConfigureAwait(false);
    }

    public async Task CachePlayers(string sender, List<string> uids, CancellationToken ct)
    {
        if (!_lastSeenCache.ContainsKey(sender)) await InitPlayer(sender).ConfigureAwait(false);

        _lastSeenCache.TryGetValue(sender, out var pairCache);
        await pairCache.CachePlayers(uids, ct).ConfigureAwait(false);
    }

    private sealed class PairCache : IDisposable
    {
        private readonly ILogger<PairCache> _logger;
        private readonly string _owner;
        private readonly MareMetrics _metrics;
        private readonly Dictionary<string, DateTime> _lastSeenCache = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _lock = new(1);

        public PairCache(ILogger<PairCache> logger, string owner, MareMetrics metrics)
        {
            metrics.IncGauge(MetricsAPI.GaugeUserPairCacheUsers);
            _logger = logger;
            _owner = owner;
            _metrics = metrics;
        }

        public async Task<bool> AreAllPlayersCached(List<string> uids, CancellationToken ct)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                var allCached = uids.TrueForAll(u => _lastSeenCache.TryGetValue(u, out var expiry) && expiry > DateTime.UtcNow);

                _logger.LogDebug("AreAllPlayersCached:{uid}:{count}:{result}", _owner, uids.Count, allCached);

                if (allCached) _metrics.IncCounter(MetricsAPI.CounterUserPairCacheHit);
                else _metrics.IncCounter(MetricsAPI.CounterUserPairCacheMiss);

                return allCached;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task CachePlayers(List<string> uids, CancellationToken ct)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var lastSeen = DateTime.UtcNow.AddMinutes(60);
                _logger.LogDebug("CacheOnlinePlayers:{uid}:{count}", _owner, uids.Count);
                var newEntries = uids.Count(u => !_lastSeenCache.ContainsKey(u));

                _metrics.IncCounter(MetricsAPI.CounterUserPairCacheNewEntries, newEntries);
                _metrics.IncCounter(MetricsAPI.CounterUserPairCacheUpdatedEntries, uids.Count - newEntries);

                _metrics.IncGauge(MetricsAPI.GaugeUserPairCacheEntries, newEntries);
                uids.ForEach(u => _lastSeenCache[u] = lastSeen);

                // clean up old entries
                var outdatedEntries = _lastSeenCache.Where(u => u.Value < DateTime.UtcNow).Select(k => k.Key).ToList();
                if (outdatedEntries.Any())
                {
                    _metrics.DecGauge(MetricsAPI.GaugeUserPairCacheEntries, outdatedEntries.Count);
                    foreach (var entry in outdatedEntries)
                    {
                        _lastSeenCache.Remove(entry);
                    }
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public void Dispose()
        {
            _metrics.DecGauge(MetricsAPI.GaugeUserPairCacheUsers);
            _metrics.DecGauge(MetricsAPI.GaugeUserPairCacheEntries, _lastSeenCache.Count);
            _lock.Dispose();
        }
    }
}
