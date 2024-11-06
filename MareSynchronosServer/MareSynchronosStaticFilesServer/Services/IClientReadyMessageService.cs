namespace MareSynchronosStaticFilesServer.Services;

public interface IClientReadyMessageService
{
    Task SendDownloadReady(string uid, Guid requestId);
}
