using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Services;

public class CharaDataCleanupService : IHostedService
{
    private readonly ILogger<CharaDataCleanupService> _logger;
    private readonly IDbContextFactory<MareDbContext> _dbContextFactory;
    private readonly CancellationTokenSource _cleanupCts = new();

    public CharaDataCleanupService(ILogger<CharaDataCleanupService> logger, IDbContextFactory<MareDbContext> dbContextFactory)
    {
        _logger = logger;
        _dbContextFactory = dbContextFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Cleanup(cancellationToken);
        return Task.CompletedTask;
    }

    private async Task Cleanup(CancellationToken ct)
    {
        _logger.LogInformation("CharaData Cleanup Service started");
        while (!ct.IsCancellationRequested)
        {
            using (var db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                var dateTime = DateTime.UtcNow;
                var expiredData = await db.CharaData.Where(c => c.ExpiryDate <= DateTime.UtcNow).ToListAsync(cancellationToken: ct).ConfigureAwait(false);

                _logger.LogInformation("Removing {count} expired Chara Data entries", expiredData.Count);

                db.RemoveRange(expiredData);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromHours(12), ct).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupCts?.Cancel();
        _cleanupCts?.Dispose();
        return Task.CompletedTask;
    }
}
