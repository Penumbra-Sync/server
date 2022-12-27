using MareSynchronosShared.Protos;
using System.Collections.Concurrent;

namespace MareSynchronosServer.Identity;

public class IdentityHandler
{
    private readonly ConcurrentDictionary<string, ServerIdentity> _cachedIdentities = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<IdentChange>> _identChanges = new(StringComparer.Ordinal);
    private readonly ILogger<IdentityHandler> _logger;

    public IdentityHandler(ILogger<IdentityHandler> logger)
    {
        _logger = logger;
    }

    internal Task<ServerIdentity> GetIdentForUid(string uid)
    {
        if (!_cachedIdentities.TryGetValue(uid, out ServerIdentity result))
        {
            result = new ServerIdentity();
        }

        return Task.FromResult(result);
    }

    internal void SetIdent(string uid, string serverId, string ident)
    {
        _cachedIdentities[uid] = new ServerIdentity() { ServerId = serverId, CharacterIdent = ident };
    }

    internal void RemoveIdent(string uid, string serverId)
    {
        if (_cachedIdentities.ContainsKey(uid) && string.Equals(_cachedIdentities[uid].ServerId, serverId, StringComparison.Ordinal))
        {
            _cachedIdentities.TryRemove(uid, out _);
        }
    }

    internal int GetOnlineUsers(string serverId)
    {
        if (string.IsNullOrEmpty(serverId))
            return _cachedIdentities.Count;
        return _cachedIdentities.Count(c => string.Equals(c.Value.ServerId, serverId, StringComparison.Ordinal));
    }

    internal Dictionary<string, ServerIdentity> GetIdentsForAllExcept(string serverId)
    {
        return _cachedIdentities.Where(k => !string.Equals(k.Value.ServerId, serverId, StringComparison.Ordinal)).ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
    }

    internal Dictionary<string, ServerIdentity> GetIdentsForServer(string serverId)
    {
        return _cachedIdentities.Where(k => string.Equals(k.Value.ServerId, serverId, StringComparison.Ordinal)).ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
    }

    internal void ClearIdentsForServer(string serverId)
    {
        var serverIdentities = _cachedIdentities.Where(i => string.Equals(i.Value.ServerId, serverId, StringComparison.Ordinal));
        foreach (var identity in serverIdentities)
        {
            _cachedIdentities.TryRemove(identity.Key, out _);
        }
    }

    internal void EnqueueIdentChange(IdentChange identchange)
    {
        _logger.LogInformation("Enqueued " + identchange.UidWithIdent.Uid.Uid + ":" + identchange.IsOnline + " from " + identchange.UidWithIdent.Ident.ServerId);

        foreach (var k in _identChanges.Keys)
        {
            if (string.Equals(k, identchange.UidWithIdent.Ident.ServerId, StringComparison.Ordinal)) continue;
            _identChanges[k].Enqueue(identchange);
        }
    }

    internal bool DequeueIdentChange(string server, out IdentChange cur)
    {
        if (!(_identChanges.ContainsKey(server) && _identChanges[server].TryDequeue(out cur)))
        {
            cur = null;
            return false;
        }

        return true;
    }

    internal void RegisterServerForQueue(string serverId)
    {
        _identChanges[serverId] = new ConcurrentQueue<IdentChange>();
    }

    internal record ServerIdentity
    {
        public string ServerId { get; set; } = string.Empty;
        public string CharacterIdent { get; set; } = string.Empty;
    }
}
