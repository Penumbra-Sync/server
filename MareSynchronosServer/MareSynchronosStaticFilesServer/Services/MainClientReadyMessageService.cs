using Microsoft.AspNetCore.SignalR;
using MareSynchronos.API.SignalR;
using MareSynchronosServer.Hubs;

namespace MareSynchronosStaticFilesServer.Services;

public class MainClientReadyMessageService : IClientReadyMessageService
{
    private readonly ILogger<MainClientReadyMessageService> _logger;
    private readonly IHubContext<MareHub> _mareHub;

    public MainClientReadyMessageService(ILogger<MainClientReadyMessageService> logger, IHubContext<MareHub> mareHub)
    {
        _logger = logger;
        _mareHub = mareHub;
    }

    public async Task SendDownloadReady(string uid, Guid requestId)
    {
        _logger.LogInformation("Sending Client Ready for {uid}:{requestId} to SignalR", uid, requestId);
        await _mareHub.Clients.User(uid).SendAsync(nameof(IMareHub.Client_DownloadReady), requestId).ConfigureAwait(false);
    }
}
