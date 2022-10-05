using Grpc.Core;
using MareSynchronosShared.Protos;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace MareSynchronosServices.Identity;

internal class IdentityService : IdentificationService.IdentificationServiceBase
{
    private readonly ILogger<IdentityService> _logger;
    private readonly IdentityHandler _handler;

    public IdentityService(ILogger<IdentityService> logger, IdentityHandler handler)
    {
        _logger = logger;
        _handler = handler;
    }

    public override Task<Empty> RemoveIdentForUid(RemoveIdentMessage request, ServerCallContext context)
    {
        _handler.RemoveIdent(request.Uid, request.ServerId);
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> SetIdentForUid(SetIdentMessage request, ServerCallContext context)
    {
        _handler.SetIdent(request.Uid, request.ServerId, request.Ident);
        return Task.FromResult(new Empty());
    }

    public override async Task<CharacterIdentMessage> GetIdentForUid(UidMessage request, ServerCallContext context)
    {
        var result = await _handler.GetIdentForuid(request.Uid);
        return new CharacterIdentMessage()
        {
            Ident = result.CharacterIdent,
            ServerId = result.ServerId
        };
    }

    public override async Task<UidMessage> GetUidForCharacterIdent(CharacterIdentMessage request, ServerCallContext context)
    {
        var result = await _handler.GetUidForCharacterIdent(request.Ident, request.ServerId);
        return new UidMessage()
        {
            Uid = result
        };
    }

    public override Task<OnlineUserCountResponse> GetOnlineUserCount(ServerMessage request, ServerCallContext context)
    {
        return Task.FromResult(new OnlineUserCountResponse() { Count = _handler.GetOnlineUsers(request.ServerId) });
    }

    public override Task<Empty> ClearIdentsForServer(ServerMessage request, ServerCallContext context)
    {
        _handler.ClearIdentsForServer(request.ServerId);
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> RecreateServerIdents(ServerIdentMessage request, ServerCallContext context)
    {
        foreach (var identMsg in request.Idents)
        {
            _handler.SetIdent(identMsg.Uid, identMsg.ServerId, identMsg.Ident);
        }
        return Task.FromResult(new Empty());
    }
}