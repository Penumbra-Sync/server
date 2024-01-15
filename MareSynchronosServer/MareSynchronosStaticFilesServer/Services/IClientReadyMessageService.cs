namespace MareSynchronosStaticFilesServer.Services;

public interface IClientReadyMessageService
{
    void SendDownloadReady(string uid, Guid requestId);
}
