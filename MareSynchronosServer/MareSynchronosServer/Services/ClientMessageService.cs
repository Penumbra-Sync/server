using Grpc.Core;
using MareSynchronos.API.Routes;
using MareSynchronosServer.Hubs;
using MareSynchronosShared.Protos;
using Microsoft.AspNetCore.SignalR;
using static MareSynchronosShared.Protos.ClientMessageService;

namespace MareSynchronosServer.Services;

public class GrpcClientMessageService : ClientMessageServiceBase
{
    private readonly ILogger<GrpcClientMessageService> _logger;
    private readonly IHubContext<MareHub, IMareHub> _hubContext;

    public GrpcClientMessageService(ILogger<GrpcClientMessageService> logger, IHubContext<MareHub, IMareHub> hubContext)
    {
        _logger = logger;
        _hubContext = hubContext;
    }

    public override async Task<Empty> SendClientMessage(ClientMessage request, ServerCallContext context)
    {
        bool hasUid = !string.IsNullOrEmpty(request.Uid);

        var severity = request.Type switch
        {
            MessageType.Info => MessageSeverity.Information,
            MessageType.Warning => MessageSeverity.Warning,
            MessageType.Error => MessageSeverity.Error,
            _ => MessageSeverity.Information,
        };

        if (!hasUid)
        {
            _logger.LogInformation("Sending Message of severity {severity} to all online users: {message}", severity, request.Message);
            await _hubContext.Clients.All.Client_ReceiveServerMessage(severity, request.Message).ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation("Sending Message of severity {severity} to user {uid}: {message}", severity, request.Uid, request.Message);
            await _hubContext.Clients.User(request.Uid).Client_ReceiveServerMessage(severity, request.Message).ConfigureAwait(false);
        }

        return new Empty();
    }
}
