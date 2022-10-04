using System.Collections.Concurrent;
using MareSynchronosShared.Metrics;

namespace MareSynchronosShared.Services;

public abstract class BaseClientIdentificationService : IClientIdentificationService
{
    private readonly MareMetrics metrics;
    protected ConcurrentDictionary<string, string> OnlineClients = new();
    protected BaseClientIdentificationService(MareMetrics metrics)
    {
        this.metrics = metrics;
    }

    public virtual Task<int> GetOnlineUsers()
    {
        return Task.FromResult(OnlineClients.Count);
    }

    public Task<string?> GetUidForCharacterIdent(string characterIdent)
    {
        var result = OnlineClients.SingleOrDefault(u =>
            string.Compare(u.Value, characterIdent, StringComparison.InvariantCultureIgnoreCase) == 0);
        return Task.FromResult(result.Equals(new KeyValuePair<string, string>()) ? null : result.Key);
    }

    public virtual Task<string?> GetCharacterIdentForUid(string uid)
    {
        if (!OnlineClients.TryGetValue(uid, out var result))
        {
            return Task.FromResult((string?)null);
        }

        return Task.FromResult(result);
    }

    public virtual Task MarkUserOnline(string uid, string charaIdent)
    {
        OnlineClients[uid] = charaIdent;
        metrics.SetGaugeTo(MetricsAPI.GaugeAuthorizedConnections, OnlineClients.Count);
        return Task.CompletedTask;
    }

    public virtual Task MarkUserOffline(string uid)
    {
        if (OnlineClients.TryRemove(uid, out _))
        {
            metrics.SetGaugeTo(MetricsAPI.GaugeAuthorizedConnections, OnlineClients.Count);
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public virtual Task StopAsync(CancellationToken cancellationToken)
    {
        metrics.SetGaugeTo(MetricsAPI.GaugeAuthorizedConnections, 0);
        OnlineClients = new();
        return Task.CompletedTask;
    }
}