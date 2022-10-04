using System.Text;
using MareSynchronosShared.Metrics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MareSynchronosShared.Services;

public class DistributedClientIdentificationService : BaseClientIdentificationService
{
    private readonly IDistributedCache distributedCache;
    private readonly ILogger<DistributedClientIdentificationService> logger;
    private readonly IConfiguration configuration;
    private const string RedisPrefix = "uidcache:";

    public DistributedClientIdentificationService(MareMetrics metrics, IDistributedCache distributedCache, IConfiguration configuration, ILogger<DistributedClientIdentificationService> logger) : base(metrics)
    {
        this.distributedCache = distributedCache;
        this.logger = logger;
        this.configuration = configuration.GetSection("MareSynchronos");
    }

    public override async Task<int> GetOnlineUsers()
    {
        try
        {
            var redis = configuration.GetValue<string>("RedisConnectionString");
            var conn = await ConnectionMultiplexer.ConnectAsync(redis).ConfigureAwait(false);
            var endpoint = conn.GetEndPoints().First();
            return await conn.GetServer(endpoint).KeysAsync(pattern: "*" + RedisPrefix + "*").CountAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during GetOnlineUsers");
            return 0;
        }
    }

    public override async Task<string?> GetCharacterIdentForUid(string uid)
    {
        var localIdent = await base.GetCharacterIdentForUid(uid).ConfigureAwait(false);
        if (localIdent != null) return localIdent;
        var cachedIdent = await distributedCache.GetStringAsync(RedisPrefix + uid).ConfigureAwait(false);
        return cachedIdent ?? null;
    }

    public override async Task MarkUserOffline(string uid)
    {
        await base.MarkUserOffline(uid).ConfigureAwait(false);
        await distributedCache.RemoveAsync(RedisPrefix + uid).ConfigureAwait(false);
    }

    public override async Task MarkUserOnline(string uid, string charaIdent)
    {
        await base.MarkUserOnline(uid, charaIdent).ConfigureAwait(false);
        await distributedCache.SetAsync(RedisPrefix + uid, Encoding.UTF8.GetBytes(charaIdent), new DistributedCacheEntryOptions()
        {
            AbsoluteExpiration = DateTime.Now.AddDays(7)
        }).ConfigureAwait(false);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var uid in OnlineClients)
        {
            distributedCache.Remove(RedisPrefix + uid.Key);
        }
        return base.StopAsync(cancellationToken);
    }
}