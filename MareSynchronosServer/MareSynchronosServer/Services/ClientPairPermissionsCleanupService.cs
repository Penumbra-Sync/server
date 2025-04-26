
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace MareSynchronosServer.Services;

public class ClientPairPermissionsCleanupService(ILogger<ClientPairPermissionsCleanupService> _logger, IDbContextFactory<MareDbContext> _dbContextFactory,
    IConfigurationService<ServerConfiguration> _configurationService)
    : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Client Pair Permissions Cleanup Service started");
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AllUsersPermissionsCleanup(CancellationToken ct)
    {
        const int MaxParallelism = 8;
        const int MaxProcessingPerChunk = 500000;

        long removedEntries = 0;
        long priorRemovedEntries = 0;
        ConcurrentDictionary<int, List<UserPermissionSet>> toRemovePermsParallel = [];
        int parallelProcessed = 0;
        int userNo = 0;

        using var db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Building All Pairs");
        var allPairs = await GetAllPairs(db, ct).ConfigureAwait(false);
        _logger.LogInformation("Found a total distinct of {count} pairs", allPairs.Values.Sum(v => v.Count));

        _logger.LogInformation("Collecting Users");
        var users = (await db.Users.Select(k => k.UID).AsNoTracking().ToListAsync(ct).ConfigureAwait(false)).Order(StringComparer.Ordinal).ToList();

        Stopwatch st = Stopwatch.StartNew();

        while (userNo < users.Count)
        {
            using CancellationTokenSource loopCts = new();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(loopCts.Token, ct);
            try
            {
                await Parallel.ForAsync(userNo, users.Count, new ParallelOptions()
                {
                    MaxDegreeOfParallelism = MaxParallelism,
                    CancellationToken = linkedCts.Token
                },
                async (i, token) =>
                {
                    var userNoInc = Interlocked.Increment(ref userNo);
                    using var db2 = await _dbContextFactory.CreateDbContextAsync(token).ConfigureAwait(false);

                    var user = users[i];
                    if (!allPairs.Remove(user, out var personalPairs))
                        personalPairs = [];

                    toRemovePermsParallel[i] = await UserPermissionCleanup(i, users.Count, user, db2, personalPairs).ConfigureAwait(false);
                    var processedAdd = Interlocked.Add(ref parallelProcessed, toRemovePermsParallel[i].Count);

                    if (userNoInc % 250 == 0)
                    {
                        var elapsed = st.Elapsed;
                        var completion = userNoInc / (double)users.Count;
                        var estimatedTimeLeft = (elapsed / completion) - elapsed;
                        _logger.LogInformation("Progress: {no}/{total} ({pct:P2}), removed so far: {removed}, planned next chunk: {planned}, estimated time left: {time}",
                            userNoInc, users.Count, completion, removedEntries, processedAdd, estimatedTimeLeft);
                    }

                    if (processedAdd > MaxProcessingPerChunk)
                        await loopCts.CancelAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }

            removedEntries += parallelProcessed;

            _logger.LogInformation("Removing {newDeleted} entities and writing to database", removedEntries - priorRemovedEntries);
            db.Permissions.RemoveRange(toRemovePermsParallel.Values.SelectMany(v => v).ToList());
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogInformation("Removed {newDeleted} entities, settling...", removedEntries - priorRemovedEntries);
            priorRemovedEntries = removedEntries;

            parallelProcessed = 0;
            toRemovePermsParallel.Clear();
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }

        st.Stop();
        _logger.LogInformation("User Permissions Cleanup Finished, removed {total} stale permissions in {time}", removedEntries, st.Elapsed);
    }

    private async Task<List<UserPermissionSet>> UserPermissionCleanup(int userNr, int totalUsers, string uid, MareDbContext dbContext, List<string> pairs)
    {
        var perms = dbContext.Permissions.Where(p => p.UserUID == uid && !p.Sticky && !pairs.Contains(p.OtherUserUID));

        var permsToRemoveCount = await perms.CountAsync().ConfigureAwait(false);
        if (permsToRemoveCount == 0)
            return [];

        _logger.LogInformation("[{current}/{totalCount}] User {user}: Planning to remove {removed} permissions", userNr, totalUsers, uid, permsToRemoveCount);

        return await perms.ToListAsync().ConfigureAwait(false);
    }

    private async Task<ConcurrentDictionary<string, List<string>>> GetAllPairs(MareDbContext dbContext, CancellationToken ct)
    {
        var entries = await dbContext.ClientPairs.AsNoTracking().Select(k => new { Self = k.UserUID, Other = k.OtherUserUID })
            .Concat(
                dbContext.GroupPairs.AsNoTracking()
                .Join(dbContext.GroupPairs.AsNoTracking(),
                    a => a.GroupGID,
                    b => b.GroupGID,
                    (a, b) => new { Self = a.GroupUserUID, Other = b.GroupUserUID })
                .Where(a => a.Self != a.Other))
            .ToListAsync(ct).ConfigureAwait(false);

        return new(entries.GroupBy(k => k.Self, StringComparer.Ordinal)
            .ToDictionary(k => k.Key, k => k.Any() ? k.Select(k => k.Other).Distinct(StringComparer.Ordinal).ToList() : [], StringComparer.Ordinal), StringComparer.Ordinal);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_configurationService.GetValueOrDefault(nameof(ServerConfiguration.RunPermissionCleanupOnStartup), defaultValue: true))
        {
            await WaitUntilNextCleanup(ct).ConfigureAwait(false);
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Starting Permissions Cleanup");
                await AllUsersPermissionsCleanup(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled Exception during User Permissions Cleanup");
            }

            await WaitUntilNextCleanup(ct).ConfigureAwait(false);
        }
    }

    private async Task WaitUntilNextCleanup(CancellationToken token)
    {
        var now = DateTime.UtcNow;
        var nextRun = new DateTime(now.Year, now.Month, now.Day, 12, 0, 0, DateTimeKind.Utc);
        if (now > nextRun) nextRun = nextRun.AddDays(1);

        var nextRunSpan = nextRun - now;
        _logger.LogInformation("Permissions Cleanup next run in {span}", nextRunSpan);

        await Task.Delay(nextRunSpan, token).ConfigureAwait(false);
    }
}
