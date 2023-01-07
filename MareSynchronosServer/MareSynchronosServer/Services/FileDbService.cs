using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Services;

public class FileDbService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<FileDbService> _logger;
    private Dictionary<string, FileCache> _fileCaches = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _semaphore = new(5);
    private readonly CancellationTokenSource _shutdownCancellationToken = new();

    public FileDbService(IServiceProvider serviceProvider, ILogger<FileDbService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<FileCache?> GetFileCache(string hash)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        if (!_fileCaches.TryGetValue(hash, out var cache))
        {
            using var db = _serviceProvider.GetService<MareDbContext>();
            cache = db.Files.AsNoTracking().SingleOrDefault(f => f.Hash == hash && f.Uploaded);
            if (cache != null) _fileCaches[hash] = cache;
        }
        _semaphore.Release();

        return cache;
    }

    private async Task UpdateDatabasePeriodically(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.Now;
            TimeOnly currentTime = new(now.Hour, now.Minute, now.Second);
            TimeOnly futureTime = new(now.Hour, now.Minute - now.Minute % 10 + 5, 0);
            var span = futureTime.AddMinutes(10) - currentTime;

            await Task.Delay(span, ct).ConfigureAwait(false);

            await UpdateDatabase().ConfigureAwait(false);
        }
    }

    private async Task UpdateDatabase()
    {
        using var scope = _serviceProvider.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<MareDbContext>();
        _fileCaches = new(await db.Files.AsNoTracking().Where(f => f.Uploaded).AsNoTracking().ToDictionaryAsync(k => k.Hash, k => k).ConfigureAwait(false), StringComparer.Ordinal);
        _logger.LogInformation("Updated FileCaches, now at {count}", _fileCaches.Count);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await UpdateDatabase().ConfigureAwait(false);
        _ = UpdateDatabasePeriodically(_shutdownCancellationToken.Token);
        _logger.LogInformation("Started FileDb Service, initially at {count} files from DB", _fileCaches.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdownCancellationToken.Cancel();

        return Task.CompletedTask;
    }
}
