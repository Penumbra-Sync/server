﻿using MareSynchronos.API.Data;
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using static Grpc.Core.Metadata;

namespace MareSynchronosServer.Services;

public class UserPairCacheService : IHostedService
{
    private readonly ConcurrentDictionary<string, Dictionary<string, UserInfo>> _cache;
    private readonly ILogger<UserPairCacheService> _logger;
    private readonly IDbContextFactory<MareDbContext> _dbContextFactory;
    private readonly ConcurrentQueue<(string? UID, string? OtherUID)> _staleUserData;
    private readonly SemaphoreSlim _lockSemaphore = new(1);
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

    public async Task<Dictionary<string, UserInfo>> GetAllPairs(string uid)
    {
        await WaitForProcessing().ConfigureAwait(false);

        if (!_cache.ContainsKey(uid))
        {
            _logger.LogDebug("Building full cache: Did not find PairData for {uid}", uid);

            using var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            _cache[uid] = await BuildFullCache(dbContext, uid).ConfigureAwait(false);
        }

        return _cache[uid];
    }

    public async Task<UserInfo> GetPairData(string uid, string otheruid)
    {
        await WaitForProcessing().ConfigureAwait(false);

        if (!_cache.TryGetValue(uid, out var cachedInfos))
        {
            _logger.LogDebug("Building full cache: Did not find PairData for {uid}:{otheruid}", uid, otheruid);
            using var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            _cache[uid] = cachedInfos = await BuildFullCache(dbContext, uid).ConfigureAwait(false);
        }

        if (!cachedInfos.TryGetValue(otheruid, out var info))
        {
            _logger.LogDebug("Building individual cache: Did not find PairData for {uid}:{otheruid}", uid, otheruid);
            using var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            info = await BuildIndividualCache(dbContext, uid, otheruid).ConfigureAwait(false);
            _cache[uid][otheruid] = info;
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
        while (_staleUserData.Any(u => string.Equals(u.UID, uid, StringComparison.Ordinal) || string.Equals(u.OtherUID, uid, StringComparison.Ordinal)))
        {
            await Task.Delay(100).ConfigureAwait(false);
        }

        var pairs = await dbContext.GetAllPairsForUser(uid).ToListAsync().ConfigureAwait(false);

        return pairs.GroupBy(g => g.OtherUserUID, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g =>
            {
                return new UserInfo(g.First().Alias,
                    g.Max(p => p.Synced),
                    g.Select(p => string.IsNullOrEmpty(p.GID) ? Constants.IndividualKeyword : p.GID).ToList(),
                    g.First().OwnPermissions,
                    g.First().OtherPermissions);
            }, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<UserInfo?> BuildIndividualCache(MareDbContext dbContext, string uid, string otheruid)
    {
        var pairs = await dbContext.GetAllPairsForUser(uid).Where(u => u.OtherUserUID == otheruid).ToListAsync().ConfigureAwait(false);

        if (!pairs.Any()) return null;

        var groups = pairs.Select(g => g.GID).ToList();
        return new UserInfo(pairs[0].Alias, pairs.Max(p => p.Synced),
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
                try
                {
                    await _lockSemaphore.WaitAsync().ConfigureAwait(false);

                    using var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
                    while (_staleUserData.TryPeek(out var staleUserPair))
                    {
                        // todo: check this spaghetti
                        if (staleUserPair.UID == null)
                        {
                            foreach (var entry in _cache.Where(c => c.Value.ContainsKey(staleUserPair.OtherUID)).ToList())
                            {
                                _logger.LogDebug("Building Full Cache for {user}", entry.Key);
                                _cache[entry.Key] = await BuildFullCache(dbContext, entry.Key).ConfigureAwait(false);
                            }
                        }
                        else if (staleUserPair.OtherUID == null)
                        {
                            if (_cache.ContainsKey(staleUserPair.UID))
                            {
                                _logger.LogDebug("Building Full Cache for {user}", staleUserPair.UID);
                                _cache[staleUserPair.UID] = await BuildFullCache(dbContext, staleUserPair.UID).ConfigureAwait(false);
                            }

                            foreach (var entry in _cache.Where(c => c.Value.ContainsKey(staleUserPair.UID)).ToList())
                            {
                                _logger.LogDebug("Building Full Cache for {user}", entry.Key);
                                _cache[entry.Key] = await BuildFullCache(dbContext, entry.Key).ConfigureAwait(false);
                            }
                        }
                        else
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

                        _staleUserData.TryDequeue(out _);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during Stale entry processing");
                }
                finally
                {
                    _lockSemaphore.Release();
                }
            }
        }
    }

    private async Task WaitForProcessing()
    {
        while (_lockSemaphore.CurrentCount == 0)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    public record UserInfo(string Alias, bool IsPaired, List<string> GIDs, UserPermissionSet? OwnPermissions, UserPermissionSet? OtherPermissions);
}