
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Data;
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
        const int MaxProcessingPerChunk = 1000000;

        long removedEntries = 0;
        long priorRemovedEntries = 0;
        ConcurrentDictionary<int, List<UserPermissionSet>> toRemovePermsParallel = [];
        ConcurrentDictionary<int, bool> completionDebugPrint = [];
        int parallelProcessed = 0;
        int userNo = 0;
        int lastUserNo = 0;

        using var db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Building All Pairs");

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
                    var personalPairs = await GetAllPairsForUser(user, db2, ct).ConfigureAwait(false);

                    toRemovePermsParallel[i] = await UserPermissionCleanup(i, users.Count, user, db2, personalPairs).ConfigureAwait(false);
                    var processedAdd = Interlocked.Add(ref parallelProcessed, toRemovePermsParallel[i].Count);

                    var completionPcnt = userNoInc / (double)users.Count;
                    var completionInt = (int)(completionPcnt * 100);

                    if (completionInt > 0 && (!completionDebugPrint.TryGetValue(completionInt, out bool posted) || !posted))
                    {
                        completionDebugPrint[completionInt] = true;
                        var elapsed = st.Elapsed;
                        var estimatedTimeLeft = (elapsed / completionPcnt) - elapsed;
                        _logger.LogInformation("Progress: {no}/{total} ({pct:P2}), removed so far: {removed}, planned next chunk: {planned}, estimated time left: {time}",
                            userNoInc, users.Count, completionPcnt, removedEntries, processedAdd, estimatedTimeLeft);
                        if (userNoInc / (double)users.Count - lastUserNo / (double)users.Count > 0.05)
                        {
                            // 5% processed without writing, might as well save at this point
                            await loopCts.CancelAsync().ConfigureAwait(false);
                        }
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

            try
            {
                parallelProcessed = 0;

                _logger.LogInformation("Removing {newDeleted} entities and writing to database", removedEntries - priorRemovedEntries);
                db.Permissions.RemoveRange(toRemovePermsParallel.Values.SelectMany(v => v).ToList());
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                _logger.LogInformation("Removed {newDeleted} entities, settling...", removedEntries - priorRemovedEntries);
                priorRemovedEntries = removedEntries;
                lastUserNo = userNo;
            }
            catch (DBConcurrencyException ex)
            {
                _logger.LogWarning(ex, "Concurrency Exception during User Permissions Cleanup, restarting at {last}", lastUserNo);
                userNo = lastUserNo;
                removedEntries = priorRemovedEntries;
                continue;
            }
            finally
            {
                toRemovePermsParallel.Clear();
            }
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

    private async Task<List<string>> GetAllPairsForUser(string uid, MareDbContext dbContext, CancellationToken ct)
    {
        var entries = await dbContext.ClientPairs.AsNoTracking().Where(k => k.UserUID == uid).Select(k => k.OtherUserUID)
            .Concat(
                dbContext.GroupPairs.Where(k => k.GroupUserUID == uid).AsNoTracking()
                .Join(dbContext.GroupPairs.AsNoTracking(),
                    a => a.GroupGID,
                    b => b.GroupGID,
                    (a, b) => b.GroupUserUID)
                .Where(a => a != uid))
            .ToListAsync(ct).ConfigureAwait(false);

        return entries.Distinct(StringComparer.Ordinal).ToList();
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
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex, "Concurrency Exception during User Permissions Cleanup");
                continue;
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
