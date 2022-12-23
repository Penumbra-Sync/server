using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using MareSynchronosShared.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MareSynchronosServices;

public class CleanupService : IHostedService, IDisposable
{
    private readonly MareMetrics metrics;
    private readonly ILogger<CleanupService> _logger;
    private readonly IServiceProvider _services;
    private readonly ServicesConfiguration _configuration;
    private Timer? _timer;

    public CleanupService(MareMetrics metrics, ILogger<CleanupService> logger, IServiceProvider services, IOptions<ServicesConfiguration> configuration)
    {
        this.metrics = metrics;
        _logger = logger;
        _services = services;
        _configuration = configuration.Value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cleanup Service started");

        _timer = new Timer(CleanUp, null, TimeSpan.Zero, TimeSpan.FromMinutes(10));

        return Task.CompletedTask;
    }

    private async void CleanUp(object state)
    {
        using var scope = _services.CreateScope();
        using var dbContext = scope.ServiceProvider.GetService<MareDbContext>()!;

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

        try
        {
            if (_configuration.PurgeUnusedAccounts)
            {
                var usersOlderThanDays = _configuration.PurgeUnusedAccountsPeriodInDays;

                _logger.LogInformation("Cleaning up users older than {usersOlderThanDays} days", usersOlderThanDays);

                var allUsers = dbContext.Users.Where(u => string.IsNullOrEmpty(u.Alias)).ToList();
                List<User> usersToRemove = new();
                foreach (var user in allUsers)
                {
                    if (user.LastLoggedIn < (DateTime.UtcNow - TimeSpan.FromDays(usersOlderThanDays)))
                    {
                        _logger.LogInformation("User outdated: {userUID}", user.UID);
                        usersToRemove.Add(user);
                    }
                }

                foreach (var user in usersToRemove)
                {
                    await PurgeUser(user, dbContext);
                }
            }

            _logger.LogInformation("Cleaning up unauthorized users");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during user purge");
        }

        try
        {
            var tempInvites = await dbContext.GroupTempInvites.ToListAsync();
            dbContext.RemoveRange(tempInvites.Where(i => i.ExpirationDate < DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during Temp Invite purge");
        }

        _logger.LogInformation($"Cleanup complete");

        dbContext.SaveChanges();
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
                    _ = await SharedDbFunctions.MigrateOrDeleteGroup(dbContext, userGroupPair.Group, groupPairs, _configuration.MaxExistingGroupsByUser).ConfigureAwait(false);
                }
            }

            dbContext.GroupPairs.Remove(userGroupPair);

            await dbContext.SaveChangesAsync();
        }

        _logger.LogInformation("User purged: {uid}", user.UID);

        dbContext.Auth.Remove(auth);
        dbContext.Users.Remove(user);

        await dbContext.SaveChangesAsync();

        metrics.DecGauge(MetricsAPI.GaugeUsersRegistered, 1);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
