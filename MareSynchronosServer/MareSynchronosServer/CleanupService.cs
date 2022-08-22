using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MareSynchronosServer.Metrics;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer
{
    public class CleanupService : IHostedService, IDisposable
    {
        private readonly ILogger<CleanupService> _logger;
        private readonly IServiceProvider _services;
        private readonly IConfiguration _configuration;
        private Timer _timer;

        public CleanupService(ILogger<CleanupService> logger, IServiceProvider services, IConfiguration configuration)
        {
            _logger = logger;
            _services = services;
            _configuration = configuration;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Cleanup Service started");

            _timer = new Timer(CleanUp, null, TimeSpan.Zero, TimeSpan.FromMinutes(10));

            return Task.CompletedTask;
        }

        private void CleanUp(object state)
        {
            if (!int.TryParse(_configuration["UnusedFileRetentionPeriodInDays"], out var filesOlderThanDays))
            {
                filesOlderThanDays = 7;
            }

            using var scope = _services.CreateScope();
            using var dbContext = scope.ServiceProvider.GetService<MareDbContext>()!;

            _logger.LogInformation("Cleaning up files older than {filesOlderThanDays} days", filesOlderThanDays);

            try
            {
                var prevTime = DateTime.Now.Subtract(TimeSpan.FromDays(filesOlderThanDays));

                var allFiles = dbContext.Files.ToList();
                foreach (var file in allFiles.Where(f => f.Uploaded))
                {
                    var fileName = Path.Combine(_configuration["CacheDirectory"], file.Hash);
                    var fi = new FileInfo(fileName);
                    if (!fi.Exists)
                    {
                        _logger.LogInformation("File does not exist anymore: {fileName}", fileName);
                        dbContext.Files.Remove(file);
                    }
                    else if (fi.LastAccessTime < prevTime)
                    {
                        MareMetrics.FilesTotalSize.Dec(fi.Length);
                        _logger.LogInformation("File outdated: {fileName}", fileName);
                        dbContext.Files.Remove(file);
                        fi.Delete();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during file cleanup");
            }

            var cacheSizeLimitInGiB = _configuration.GetValue<double>("CacheSizeHardLimitInGiB", -1);

            try
            {
                if (cacheSizeLimitInGiB > 0)
                {
                    _logger.LogInformation("Cleaning up files beyond the cache size limit");
                    var allLocalFiles = Directory.EnumerateFiles(_configuration["CacheDirectory"]).Select(f => new FileInfo(f)).ToList().OrderBy(f => f.LastAccessTimeUtc).ToList();
                    var totalCacheSizeInBytes = allLocalFiles.Sum(s => s.Length);
                    long cacheSizeLimitInBytes = (long)(cacheSizeLimitInGiB * 1024 * 1024 * 1024);
                    HashSet<string> removedHashes = new();
                    while (totalCacheSizeInBytes > cacheSizeLimitInBytes && allLocalFiles.Any())
                    {
                        var oldestFile = allLocalFiles.First();
                        removedHashes.Add(oldestFile.Name.ToLower());
                        allLocalFiles.Remove(oldestFile);
                        totalCacheSizeInBytes -= oldestFile.Length;
                        MareMetrics.FilesTotal.Dec();
                        MareMetrics.FilesTotalSize.Dec(oldestFile.Length);
                        oldestFile.Delete();
                    }

                    dbContext.Files.RemoveRange(dbContext.Files.Where(f => removedHashes.Contains(f.Hash.ToLower())));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during cache size limit cleanup");
            }

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

                dbContext.RemoveRange(expiredAuths.Select(a => a.User));
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

                    var allUsers = dbContext.Users.ToList();
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
                        PurgeUser(user, dbContext, _configuration);
                    }
                }

                _logger.LogInformation("Cleaning up unauthorized users");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during user purge");
            }

            SecretKeyAuthenticationHandler.ClearUnauthorizedUsers();

            _logger.LogInformation($"Cleanup complete");

            dbContext.SaveChanges();
        }

        public static void PurgeUser(User user, MareDbContext dbContext, IConfiguration _configuration)
        {
            var lodestone = dbContext.LodeStoneAuth.SingleOrDefault(a => a.User.UID == user.UID);

            if (lodestone != null)
            {
                dbContext.Remove(lodestone);
            }

            SecretKeyAuthenticationHandler.RemoveAuthentication(user.UID);

            var auth = dbContext.Auth.Single(a => a.UserUID == user.UID);

            var userFiles = dbContext.Files.Where(f => f.Uploaded && f.Uploader.UID == user.UID).ToList();
            foreach (var file in userFiles)
            {
                var fi = new FileInfo(Path.Combine(_configuration["CacheDirectory"], file.Hash));
                if (fi.Exists)
                {
                    MareMetrics.FilesTotalSize.Dec(fi.Length);
                    MareMetrics.FilesTotal.Dec();
                    fi.Delete();
                }
            }

            dbContext.Files.RemoveRange(userFiles);

            var ownPairData = dbContext.ClientPairs.Where(u => u.User.UID == user.UID).ToList();

            dbContext.RemoveRange(ownPairData);
            var otherPairData = dbContext.ClientPairs.Include(u => u.User)
                .Where(u => u.OtherUser.UID == user.UID).ToList();

            MareMetrics.Pairs.Dec(ownPairData.Count);
            MareMetrics.PairsPaused.Dec(ownPairData.Count(c => c.IsPaused));
            MareMetrics.Pairs.Dec(otherPairData.Count);
            MareMetrics.PairsPaused.Dec(otherPairData.Count(c => c.IsPaused));
            MareMetrics.UsersRegistered.Dec();

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
