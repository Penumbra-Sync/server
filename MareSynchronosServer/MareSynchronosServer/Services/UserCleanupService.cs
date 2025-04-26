using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Services;

public class UserCleanupService : IHostedService
{
    private readonly MareMetrics metrics;
    private readonly ILogger<UserCleanupService> _logger;
    private readonly IDbContextFactory<MareDbContext> _mareDbContextFactory;
    private readonly IConfigurationService<ServerConfiguration> _configuration;
    private CancellationTokenSource _cleanupCts;

    public UserCleanupService(MareMetrics metrics, ILogger<UserCleanupService> logger, IDbContextFactory<MareDbContext> mareDbContextFactory, IConfigurationService<ServerConfiguration> configuration)
    {
        this.metrics = metrics;
        _logger = logger;
        _mareDbContextFactory = mareDbContextFactory;
        _configuration = configuration;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cleanup Service started");
        _cleanupCts = new();

        _ = CleanUp(_cleanupCts.Token);

        return Task.CompletedTask;
    }

    private async Task CleanUp(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using (var dbContext = await _mareDbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
            {

                CleanUpOutdatedLodestoneAuths(dbContext);

                await PurgeUnusedAccounts(dbContext).ConfigureAwait(false);

                await PurgeTempInvites(dbContext).ConfigureAwait(false);

                dbContext.SaveChanges();
            }

            var now = DateTime.Now;
            TimeOnly currentTime = new(now.Hour, now.Minute, now.Second);
            TimeOnly futureTime = new(now.Hour, now.Minute - now.Minute % 10, 0);
            var span = futureTime.AddMinutes(10) - currentTime;

            _logger.LogInformation("User Cleanup Complete, next run at {date}", now.Add(span));
            await Task.Delay(span, ct).ConfigureAwait(false);
        }
    }

    private async Task PurgeTempInvites(MareDbContext dbContext)
    {
        try
        {
            var tempInvites = await dbContext.GroupTempInvites.ToListAsync().ConfigureAwait(false);
            dbContext.RemoveRange(tempInvites.Where(i => i.ExpirationDate < DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during Temp Invite purge");
        }
    }

    private async Task PurgeUnusedAccounts(MareDbContext dbContext)
    {
        try
        {
            if (_configuration.GetValueOrDefault(nameof(ServerConfiguration.PurgeUnusedAccounts), false))
            {
                var usersOlderThanDays = _configuration.GetValueOrDefault(nameof(ServerConfiguration.PurgeUnusedAccountsPeriodInDays), 14);
                var maxGroupsByUser = _configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxGroupUserCount), 3);

                _logger.LogInformation("Cleaning up users older than {usersOlderThanDays} days", usersOlderThanDays);

                var allUsers = dbContext.Users.Where(u => string.IsNullOrEmpty(u.Alias)).ToList();
                List<User> usersToRemove = new();
                foreach (var user in allUsers)
                {
                    if (user.LastLoggedIn < DateTime.UtcNow - TimeSpan.FromDays(usersOlderThanDays))
                    {
                        _logger.LogInformation("User outdated: {userUID}", user.UID);
                        usersToRemove.Add(user);
                    }
                }

                foreach (var user in usersToRemove)
                {
                    await SharedDbFunctions.PurgeUser(_logger, user, dbContext, maxGroupsByUser).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during user purge");
        }
    }

    private void CleanUpOutdatedLodestoneAuths(MareDbContext dbContext)
    {
        try
        {
            _logger.LogInformation($"Cleaning up expired lodestone authentications");
            var lodestoneAuths = dbContext.LodeStoneAuth.Include(u => u.User).Where(a => a.StartedAt != null).ToList();
            List<LodeStoneAuth> expiredAuths = new List<LodeStoneAuth>();
            foreach (var auth in lodestoneAuths)
            {
                if (auth.StartedAt < DateTime.UtcNow - TimeSpan.FromMinutes(15))
                {
                    expiredAuths.Add(auth);
                }
            }

            dbContext.Users.RemoveRange(expiredAuths.Where(u => u.User != null).Select(a => a.User));
            dbContext.RemoveRange(expiredAuths);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during expired auths cleanup");
        }
    }

    public async Task PurgeUser(User user, MareDbContext dbContext)
    {
        _logger.LogInformation("Purging user: {uid}", user.UID);

        var lodestone = dbContext.LodeStoneAuth.SingleOrDefault(a => a.User.UID == user.UID);

        if (lodestone != null)
        {
            dbContext.Remove(lodestone);
        }

        var auth = dbContext.Auth.Single(a => a.UserUID == user.UID);

        var userFiles = dbContext.Files.Where(f => f.Uploaded && f.Uploader.UID == user.UID).ToList();
        dbContext.Files.RemoveRange(userFiles);

        var ownPairData = dbContext.ClientPairs.Where(u => u.User.UID == user.UID).ToList();
        dbContext.ClientPairs.RemoveRange(ownPairData);
        var otherPairData = dbContext.ClientPairs.Include(u => u.User)
            .Where(u => u.OtherUser.UID == user.UID).ToList();
        dbContext.ClientPairs.RemoveRange(otherPairData);

        var userJoinedGroups = await dbContext.GroupPairs.Include(g => g.Group).Where(u => u.GroupUserUID == user.UID).ToListAsync().ConfigureAwait(false);

        foreach (var userGroupPair in userJoinedGroups)
        {
            bool ownerHasLeft = string.Equals(userGroupPair.Group.OwnerUID, user.UID, StringComparison.Ordinal);

            if (ownerHasLeft)
            {
                var groupPairs = await dbContext.GroupPairs.Where(g => g.GroupGID == userGroupPair.GroupGID && g.GroupUserUID != user.UID).ToListAsync().ConfigureAwait(false);

                if (!groupPairs.Any())
                {
                    _logger.LogInformation("Group {gid} has no new owner, deleting", userGroupPair.GroupGID);
                    dbContext.Groups.Remove(userGroupPair.Group);
                }
                else
                {
                    _ = await SharedDbFunctions.MigrateOrDeleteGroup(dbContext, userGroupPair.Group, groupPairs, _configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxExistingGroupsByUser), 3)).ConfigureAwait(false);
                }
            }

            dbContext.GroupPairs.Remove(userGroupPair);
        }

        _logger.LogInformation("User purged: {uid}", user.UID);

        dbContext.Auth.Remove(auth);
        dbContext.Users.Remove(user);

        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupCts.Cancel();

        return Task.CompletedTask;
    }
}
