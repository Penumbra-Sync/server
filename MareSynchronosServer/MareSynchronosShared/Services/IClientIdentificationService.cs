using Microsoft.Extensions.Hosting;

namespace MareSynchronosShared.Services;

public interface IClientIdentificationService : IHostedService
{
    int GetOnlineUsers();
    string? GetUidForCharacterIdent(string characterIdent);
    string? GetCharacterIdentForUid(string uid);
    void MarkUserOnline(string uid, string charaIdent);
    void MarkUserOffline(string uid);
}