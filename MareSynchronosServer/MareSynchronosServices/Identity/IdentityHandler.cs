using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MareSynchronosServices.Identity;

internal class IdentityHandler
{
    private readonly ConcurrentDictionary<string, ServerIdentity> cachedIdentities = new();

    internal Task<string> GetUidForCharacterIdent(string ident, string serverId)
    {
        var exists = cachedIdentities.Any(f => f.Value.CharacterIdent == ident && f.Value.ServerId == serverId);
        return Task.FromResult(exists ? cachedIdentities.FirstOrDefault(f => f.Value.CharacterIdent == ident && f.Value.ServerId == serverId).Key : string.Empty);
    }

    internal Task<ServerIdentity> GetIdentForuid(string uid)
    {
        ServerIdentity result;
        if (!cachedIdentities.TryGetValue(uid, out result))
        {
            result = new ServerIdentity();
        }

        return Task.FromResult(result);
    }

    internal void SetIdent(string uid, string serverId, string ident)
    {
        cachedIdentities[uid] = new ServerIdentity() { ServerId = serverId, CharacterIdent = ident };
    }

    internal void RemoveIdent(string uid, string serverId)
    {
        if (cachedIdentities.ContainsKey(uid) && cachedIdentities[uid].ServerId == serverId)
        {
            cachedIdentities.TryRemove(uid, out _);
        }
    }

    internal int GetOnlineUsers(string serverId)
    {
        if (string.IsNullOrEmpty(serverId))
            return cachedIdentities.Count;
        return cachedIdentities.Count(c => c.Value.ServerId == serverId);
    }

    internal Dictionary<string, ServerIdentity> GetIdentsForAllExcept(string serverId)
    {
        return cachedIdentities.Where(k => k.Value.ServerId != serverId).ToDictionary(k => k.Key, k => k.Value);
    }

    internal Dictionary<string, ServerIdentity> GetIdentsForServer(string serverId)
    {
        return cachedIdentities.Where(k => k.Value.ServerId == serverId).ToDictionary(k => k.Key, k => k.Value);
    }

    internal void ClearIdentsForServer(string serverId)
    {
        var serverIdentities = cachedIdentities.Where(i => i.Value.ServerId == serverId);
        foreach (var identity in serverIdentities)
        {
            cachedIdentities.TryRemove(identity.Key, out _);
        }
    }
}

internal record ServerIdentity
{
    public string ServerId { get; set; } = string.Empty;
    public string CharacterIdent { get; set; } = string.Empty;
}
