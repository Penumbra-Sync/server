namespace MareSynchronosServer.Services;

public interface IClientIdentificationService : IHostedService
{
    string GetCharacterIdentForUid(string uid);
    Task<long> GetOnlineUsers();
    string GetServerForUid(string uid);
    bool IsOnCurrentServer(string uid);
    void MarkUserOffline(string uid);
    void MarkUserOnline(string uid, string charaIdent);
}
