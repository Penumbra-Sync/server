using Microsoft.Extensions.Hosting;

namespace MareSynchronosShared.Services;

public interface IClientIdentificationService : IHostedService
{
    Task<int> GetOnlineUsers();
    Task<string?> GetUidForCharacterIdent(string characterIdent);
    Task<string?> GetCharacterIdentForUid(string uid);
    Task MarkUserOnline(string uid, string charaIdent);
    Task MarkUserOffline(string uid);
}