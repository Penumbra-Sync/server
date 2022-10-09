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

    public override async Task<CharacterIdentMessage> GetIdentForUid(UidMessage request, ServerCallContext context)
    {
        var result = await _handler.GetIdentForuid(request.Uid);
        return new CharacterIdentMessage()
        {
            Ident = result.CharacterIdent,
            ServerId = result.ServerId
        };
    }

    public override Task<OnlineUserCountResponse> GetOnlineUserCount(ServerMessage request, ServerCallContext context)
    {
        return Task.FromResult(new OnlineUserCountResponse() { Count = _handler.GetOnlineUsers(request.ServerId) });
    }

    public override Task<Empty> ClearIdentsForServer(ServerMessage request, ServerCallContext context)
    {
        var idents = _handler.GetIdentsForServer(request.ServerId);
        foreach (var entry in idents)
        {
            EnqueueIdentOffline(new UidWithIdent()
            {
                Ident = new CharacterIdentMessage()
                {
                    Ident = entry.Value.CharacterIdent,
                    ServerId = entry.Value.ServerId
                },
                Uid = new UidMessage()
                {
                    Uid = entry.Key
                }
            });
        }

        _handler.ClearIdentsForServer(request.ServerId);
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> RecreateServerIdents(ServerIdentMessage request, ServerCallContext context)
    {
        foreach (var identMsg in request.Idents)
        {
            _handler.SetIdent(identMsg.UidWithIdent.Uid.Uid, identMsg.UidWithIdent.Ident.ServerId, identMsg.UidWithIdent.Ident.Ident);
            EnqueueIdentOnline(identMsg.UidWithIdent);
        }
        return Task.FromResult(new Empty());
    }

    public override async Task<Empty> SendStreamIdentStatusChange(IAsyncStreamReader<IdentChangeMessage> requestStream, ServerCallContext context)
    {
        await requestStream.MoveNext();
        var server = requestStream.Current.Server;
        if (server == null) throw new System.Exception("First message needs to be server message");
        _handler.RegisterServerForQueue(server.ServerId);
        _logger.LogInformation("Registered Server " + server.ServerId + " input stream");

        while (await requestStream.MoveNext().ConfigureAwait(false))
        {
            var cur = requestStream.Current.IdentChange;
            if (cur == null) throw new System.Exception("Expected client ident change");
            _handler.EnqueueIdentChange(cur);

            if (cur.IsOnline)
            {
                _handler.SetIdent(cur.UidWithIdent.Uid.Uid, cur.UidWithIdent.Ident.ServerId, cur.UidWithIdent.Ident.Ident);
            }
            else
            {
                _handler.RemoveIdent(cur.UidWithIdent.Uid.Uid, cur.UidWithIdent.Ident.ServerId);
            }
        }

        _logger.LogInformation("Server input stream from " + server + " finished");

        return new Empty();
    }

    public override async Task ReceiveStreamIdentStatusChange(ServerMessage request, IServerStreamWriter<IdentChange> responseStream, ServerCallContext context)
    {
        var server = request.ServerId;
        _logger.LogInformation("Registered Server " + server + " output stream");

        try
        {
            while (!context.CancellationToken.IsCancellationRequested)
            {
                while (_handler.DequeueIdentChange(server, out var cur))
                {
                    await responseStream.WriteAsync(cur).ConfigureAwait(false);
                }

                await Task.Delay(10).ConfigureAwait(false);
            }
        }
        catch
        {
            _logger.LogInformation("Server output stream to " + server + " is faulty");
        }

        _logger.LogInformation("Server output stream to " + server + " is finished");
    }

    public override Task<UidWithIdentMessage> GetAllIdents(ServerMessage request, ServerCallContext context)
    {
        var response = new UidWithIdentMessage();
        foreach (var item in _handler.GetIdentsForAllExcept(request.ServerId))
        {
            response.UidWithIdent.Add(new UidWithIdent()
            {
                Uid = new UidMessage()
                {
                    Uid = item.Key
                },
                Ident = new CharacterIdentMessage()
                {
                    Ident = item.Value.CharacterIdent,
                    ServerId = item.Value.ServerId
                }
            });
        }

        return Task.FromResult(response);
    }

    private void EnqueueIdentOnline(UidWithIdent ident)
    {
        _handler.EnqueueIdentChange(new IdentChange()
        {
            IsOnline = true,
            UidWithIdent = ident
        });
    }

    private void EnqueueIdentOffline(UidWithIdent ident)
    {
        _handler.EnqueueIdentChange(new IdentChange()
        {
            IsOnline = false,
            UidWithIdent = ident
        });
    }
}