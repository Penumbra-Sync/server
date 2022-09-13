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

    public virtual int GetOnlineUsers()
    {
        return OnlineClients.Count;
    }

    public string? GetUidForCharacterIdent(string characterIdent)
    {
        var result = OnlineClients.SingleOrDefault(u =>
            string.Compare(u.Value, characterIdent, StringComparison.InvariantCultureIgnoreCase) == 0);
        return result.Equals(new KeyValuePair<string, string>()) ? null : result.Key;
    }

    public virtual string? GetCharacterIdentForUid(string uid)
    {
        if (!OnlineClients.TryGetValue(uid, out var result))
        {
            return null;
        }

        return result;
    }

    public virtual void MarkUserOnline(string uid, string charaIdent)
    {
        OnlineClients[uid] = charaIdent;
        metrics.SetGaugeTo(MetricsAPI.GaugeAuthorizedConnections, OnlineClients.Count);
    }

    public virtual void MarkUserOffline(string uid)
    {
        if (OnlineClients.TryRemove(uid, out _))
        {
            metrics.SetGaugeTo(MetricsAPI.GaugeAuthorizedConnections, OnlineClients.Count);
        }
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