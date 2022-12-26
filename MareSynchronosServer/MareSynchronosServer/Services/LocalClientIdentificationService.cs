using MareSynchronosShared.Protos;
using System.Collections.Concurrent;
using MareSynchronosServer.Identity;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;

namespace MareSynchronosServer.Services;

public class LocalClientIdentificationService : IClientIdentificationService
{
    protected readonly ConcurrentDictionary<string, UidWithIdent> OnlineClients = new(StringComparer.Ordinal);
    private readonly IdentityHandler _identityHandler;
    private readonly string _shardName;

    public LocalClientIdentificationService(IdentityHandler identityHandler, IConfigurationService<ServerConfiguration> config)
    {
        _identityHandler = identityHandler;
        _shardName = config.GetValueOrDefault(nameof(ServerConfiguration.ShardName), string.Empty);
    }

    public string GetCharacterIdentForUid(string uid)
    {
        return _identityHandler.GetIdentForUid(uid).Result.CharacterIdent;
    }

    public Task<long> GetOnlineUsers()
    {
        return Task.FromResult((long)_identityHandler.GetOnlineUsers(string.Empty));
    }

    public string GetServerForUid(string uid)
    {
        return _identityHandler.GetIdentForUid(uid).Result.ServerId;
    }

    public bool IsOnCurrentServer(string uid)
    {
        return string.Equals(_identityHandler.GetIdentForUid(uid).Result.ServerId, _shardName, StringComparison.Ordinal);
    }

    public void MarkUserOffline(string uid)
    {
        _identityHandler.RemoveIdent(uid, _shardName);
    }

    public void MarkUserOnline(string uid, string charaIdent)
    {
        _identityHandler.SetIdent(uid, _shardName, charaIdent);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}