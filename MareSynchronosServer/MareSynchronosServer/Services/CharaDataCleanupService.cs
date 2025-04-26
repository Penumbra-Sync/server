using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Services;

public class CharaDataCleanupService : BackgroundService
{
    private readonly ILogger<CharaDataCleanupService> _logger;
    private readonly IDbContextFactory<MareDbContext> _dbContextFactory;

    public CharaDataCleanupService(ILogger<CharaDataCleanupService> logger, IDbContextFactory<MareDbContext> dbContextFactory)
    {
        _logger = logger;
        _dbContextFactory = dbContextFactory;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Chara Data Cleanup Service started");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
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
}
