using MareSynchronosServices.Authentication;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MareSynchronosServices
{
    public class CleanupService : IHostedService, IDisposable
    {
        private readonly MareMetrics metrics;
        private readonly SecretKeyAuthenticationHandler _authService;
        private readonly ILogger<CleanupService> _logger;
        private readonly IServiceProvider _services;
        private readonly IConfiguration _configuration;
        private Timer? _timer;

        public CleanupService(MareMetrics metrics, SecretKeyAuthenticationHandler authService, ILogger<CleanupService> logger, IServiceProvider services, IConfiguration configuration)
        {
            this.metrics = metrics;
            _authService = authService;
            _logger = logger;
            _services = services;
            _configuration = configuration.GetRequiredSection("MareSynchronos");
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Cleanup Service started");

            _timer = new Timer(CleanUp, null, TimeSpan.Zero, TimeSpan.FromMinutes(10));

            return Task.CompletedTask;
        }

        private void CleanUp(object state)
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
                if (!bool.TryParse(_configuration["PurgeUnusedAccounts"], out var purgeUnusedAccounts))
                {
                    purgeUnusedAccounts = false;
                }

                if (purgeUnusedAccounts)
                {
                    if (!int.TryParse(_configuration["PurgeUnusedAccountsPeriodInDays"], out var usersOlderThanDays))
                    {
                        usersOlderThanDays = 14;
                    }

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
                        PurgeUser(user, dbContext);
                    }
                }

                _logger.LogInformation("Cleaning up unauthorized users");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during user purge");
            }

            _authService.ClearUnauthorizedUsers();

            _logger.LogInformation($"Cleanup complete");

            dbContext.SaveChanges();
        }

        public void PurgeUser(User user, MareDbContext dbContext)
        {
            var lodestone = dbContext.LodeStoneAuth.SingleOrDefault(a => a.User.UID == user.UID);

            if (lodestone != null)
            {
                dbContext.Remove(lodestone);
            }

            _authService.RemoveAuthentication(user.UID);

            var auth = dbContext.Auth.Single(a => a.UserUID == user.UID);

            var userFiles = dbContext.Files.Where(f => f.Uploaded && f.Uploader.UID == user.UID).ToList();
            dbContext.Files.RemoveRange(userFiles);

            var ownPairData = dbContext.ClientPairs.Where(u => u.User.UID == user.UID).ToList();

            dbContext.RemoveRange(ownPairData);
            var otherPairData = dbContext.ClientPairs.Include(u => u.User)
                .Where(u => u.OtherUser.UID == user.UID).ToList();


            metrics.DecGauge(MetricsAPI.GaugePairs, ownPairData.Count + otherPairData.Count);
            metrics.DecGauge(MetricsAPI.GaugePairsPaused, ownPairData.Count + ownPairData.Count(c => c.IsPaused));
            metrics.DecGauge(MetricsAPI.GaugeUsersRegistered, ownPairData.Count + 1);

            dbContext.RemoveRange(otherPairData);
            dbContext.Remove(auth);
            dbContext.Remove(user);
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
}
