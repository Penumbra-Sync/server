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

    public override int GetOnlineUsers()
    {
        try
        {
            var redis = configuration.GetValue<string>("RedisConnectionString");
            var conn = ConnectionMultiplexer.Connect(redis);
            var endpoint = conn.GetEndPoints().First();
            return conn.GetServer(endpoint).Keys(pattern: "*" + RedisPrefix + "*").Count();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during GetOnlineUsers");
            return 0;
        }
    }

    public override string? GetCharacterIdentForUid(string uid)
    {
        var localIdent = base.GetCharacterIdentForUid(uid);
        if (localIdent != null) return localIdent;
        var cachedIdent = distributedCache.Get(RedisPrefix + uid);
        return cachedIdent == null ? null : Encoding.UTF8.GetString(cachedIdent);
    }

    public override void MarkUserOffline(string uid)
    {
        base.MarkUserOffline(uid);
        distributedCache.Remove(RedisPrefix + uid);
    }

    public override void MarkUserOnline(string uid, string charaIdent)
    {
        base.MarkUserOnline(uid, charaIdent);
        distributedCache.Set(RedisPrefix + uid, Encoding.UTF8.GetBytes(charaIdent));
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