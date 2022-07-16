using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MareSynchronosServer.Data;
using MareSynchronosServer.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer
{
    public class FileCleanupService : IHostedService, IDisposable
    {
        private readonly ILogger<FileCleanupService> _logger;
        private readonly IServiceProvider _services;
        private readonly IConfiguration _configuration;
        private Timer _timer;

        public FileCleanupService(ILogger<FileCleanupService> logger, IServiceProvider services, IConfiguration configuration)
        {
            _logger = logger;
            _services = services;
            _configuration = configuration;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("File Cleanup Service started");

            _timer = new Timer(CleanUpFiles, null, TimeSpan.Zero, TimeSpan.FromMinutes(10));

            return Task.CompletedTask;
        }

        private void CleanUpFiles(object state)
        {
            if (!int.TryParse(_configuration["UnusedFileRetentionPeriodInDays"], out var filesOlderThanDays))
            {
                filesOlderThanDays = 7;
            }

            _logger.LogInformation($"Cleaning up files older than {filesOlderThanDays} days");

            try
            {
                using var scope = _services.CreateScope();
                var dbContext = scope.ServiceProvider.GetService<MareDbContext>()!;

                var prevTime = DateTime.Now.Subtract(TimeSpan.FromDays(filesOlderThanDays));

                var allFiles = dbContext.Files.Where(f => f.Uploaded).ToList();
                foreach (var file in allFiles)
                {
                    var fileName = Path.Combine(_configuration["CacheDirectory"], file.Hash);
                    var fi = new FileInfo(fileName);
                    if (!fi.Exists)
                    {
                        _logger.LogInformation("File does not exist anymore: " + fileName);
                        dbContext.Files.Remove(file);
                    } else if (fi.LastAccessTime < prevTime)
                    {
                        MareMetrics.FilesTotalSize.Dec(fi.Length);
                        _logger.LogInformation("File outdated: " + fileName);
                        dbContext.Files.Remove(file);
                        File.Delete(fileName);
                    }
                }

                _logger.LogInformation($"Cleanup complete");

                dbContext.SaveChanges();
            }
            catch
            {
            }
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
