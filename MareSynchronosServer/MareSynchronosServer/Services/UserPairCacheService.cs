using MareSynchronos.API.Data;
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace MareSynchronosServer.Services;

public class UserPairCacheService : IHostedService
{
    private readonly ConcurrentDictionary<string, Dictionary<string, UserInfo>> _cache;
    private readonly ILogger<UserPairCacheService> _logger;
    private readonly IDbContextFactory<MareDbContext> _dbContextFactory;
    private readonly ConcurrentQueue<(string? UID, string? OtherUID)> _staleUserData;
    public UserPairCacheService(ILogger<UserPairCacheService> logger, IDbContextFactory<MareDbContext> dbContextFactory)
    {
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _staleUserData = new();
        _cache = new(StringComparer.OrdinalIgnoreCase);
    }

    public void ClearCache(string uid)
    {
        _cache.TryRemove(uid, out _);
    }

    public async Task<Dictionary<string, UserInfo>> GetAllPairs(string uid, MareDbContext dbContext)
    {
        await WaitForProcessing(uid).ConfigureAwait(false);

        if (!_cache.ContainsKey(uid))
        {
            _logger.LogDebug("Building full cache: Did not find PairData for {uid}", uid);

            _cache[uid] = await BuildFullCache(dbContext, uid).ConfigureAwait(false);
        }

        return _cache[uid];
    }

    public async Task<UserInfo?> GetPairData(string uid, string otheruid, MareDbContext dbContext)
    {
        await WaitForProcessing(uid, otheruid).ConfigureAwait(false);

        if (!_cache.TryGetValue(uid, out var cachedInfos))
        {
            _logger.LogDebug("Building full cache: Did not find PairData for {uid}:{otheruid}", uid, otheruid);
            try
            {
                _cache[uid] = cachedInfos = await BuildFullCache(dbContext, uid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during full PairCache calculation of {uid}:{otheruid}", uid, otheruid);
                return null;
            }
        }

        if (!cachedInfos.TryGetValue(otheruid, out var info))
        {
            _logger.LogDebug("Building individual cache: Did not find PairData for {uid}:{otheruid}", uid, otheruid);
            try
            {
                info = await BuildIndividualCache(dbContext, uid, otheruid).ConfigureAwait(false);
                _cache[uid][otheruid] = info;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during individual PairCache calculation of {uid}:{otheruid}", uid, otheruid);
                return null;
            }
        }

        return info;
    }

    public void MarkAsStale(string? uid, string? otheruid)
    {
        if (!_staleUserData.Any(u => string.Equals(u.UID, uid, StringComparison.Ordinal)
            && string.Equals(u.OtherUID, otheruid, StringComparison.OrdinalIgnoreCase)))
        {
            _staleUserData.Enqueue((uid, otheruid));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = ProcessStaleEntries();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task<Dictionary<string, UserInfo>> BuildFullCache(MareDbContext dbContext, string uid)
    {
        var pairs = await dbContext.GetAllPairsForUser(uid).ToListAsync().ConfigureAwait(false);

        return pairs.GroupBy(g => g.OtherUserUID, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g =>
            {
                return new UserInfo(g.First().Alias,
                    g.SingleOrDefault(p => string.IsNullOrEmpty(p.GID))?.Synced ?? false,
                    g.Max(p => p.Synced),
                    g.Select(p => string.IsNullOrEmpty(p.GID) ? Constants.IndividualKeyword : p.GID).ToList(),
                    g.First().OwnPermissions,
                    g.First().OtherPermissions);
            }, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<UserInfo?> BuildIndividualCache(MareDbContext dbContext, string uid, string otheruid)
    {
        var pairs = (await dbContext.GetAllPairsForUser(uid).ToListAsync().ConfigureAwait(false)).Where(u => string.Equals(u.OtherUserUID, otheruid, StringComparison.Ordinal)).ToList();

        if (!pairs.Any()) return null;

        var groups = pairs.Select(g => g.GID).ToList();
        return new UserInfo(pairs[0].Alias,
            pairs.SingleOrDefault(p => string.IsNullOrEmpty(p.GID))?.Synced ?? false,
            pairs.Max(p => p.Synced),
            pairs.Select(p => string.IsNullOrEmpty(p.GID) ? Constants.IndividualKeyword : p.GID).ToList(),
            pairs[0].OwnPermissions,
            pairs[0].OtherPermissions);
    }

    private async Task ProcessStaleEntries()
    {
        while (true)
        {
            await Task.Delay(250).ConfigureAwait(false);
            if (_staleUserData.Any())
            {
                _logger.LogDebug("Processing Stale Entries");

                using var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
                while (_staleUserData.TryDequeue(out var staleUserPair))
                {
                    try
                    {
                        if (staleUserPair.UID == null)
                        {
                            foreach (var entry in _cache.Where(c => c.Value.ContainsKey(staleUserPair.OtherUID)).Select(k => k.Key).Distinct(StringComparer.Ordinal).ToList())
                            {
                                _logger.LogDebug("UID is null; Building Individual Cache for {user}:{user2}", staleUserPair.UID, entry);
                                _staleUserData.Enqueue(new(staleUserPair.OtherUID, entry));
                            }
                        }
                        else if (staleUserPair.OtherUID == null)
                        {
                            foreach (var entry in _cache.Where(c => c.Value.ContainsKey(staleUserPair.UID)).Select(k => k.Key).Distinct(StringComparer.Ordinal).ToList())
                            {
                                _logger.LogDebug("OtherUID is null; Building Individual Cache for {user}:{user2}", staleUserPair.UID, entry);
                                _staleUserData.Enqueue(new(staleUserPair.UID, entry));
                            }
                        }
                        else
                        {
                            if (_cache.ContainsKey(staleUserPair.UID))
                            {
                                _logger.LogDebug("Building Individual Cache for {user}:{user2}", staleUserPair.UID, staleUserPair.OtherUID);

                                var userInfo = await BuildIndividualCache(dbContext, staleUserPair.UID, staleUserPair.OtherUID).ConfigureAwait(false);

                                if (userInfo == null) _cache[staleUserPair.UID].Remove(staleUserPair.OtherUID);
                                else _cache[staleUserPair.UID][staleUserPair.OtherUID] = userInfo;

                                if (_cache.ContainsKey(staleUserPair.OtherUID))
                                {
                                    _logger.LogDebug("Building Individual Cache for {user}:{user2}", staleUserPair.OtherUID, staleUserPair.UID);
                                    var otherUserInfo = await BuildIndividualCache(dbContext, staleUserPair.OtherUID, staleUserPair.UID).ConfigureAwait(false);
                                    if (otherUserInfo == null) _cache[staleUserPair.OtherUID].Remove(staleUserPair.UID);
                                    else _cache[staleUserPair.OtherUID][staleUserPair.UID] = otherUserInfo;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during Stale entry processing");
                    }
                }

            }
        }
    }

    private async Task WaitForProcessing(string uid)
    {
        while (_staleUserData.Any(u => string.Equals(u.UID, uid, StringComparison.Ordinal)))
        {
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private async Task WaitForProcessing(string uid, string otheruid)
    {
        while (_staleUserData.Any(u => string.Equals(u.UID, uid, StringComparison.Ordinal) && string.Equals(u.OtherUID, otheruid, StringComparison.Ordinal)))
        {
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    public record UserInfo(string Alias, bool IndividuallyPaired, bool IsSynced, List<string> GIDs, UserPermissionSet? OwnPermissions, UserPermissionSet? OtherPermissions);
}
