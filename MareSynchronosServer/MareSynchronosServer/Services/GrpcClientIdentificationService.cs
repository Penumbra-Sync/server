using Grpc.Core;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Protos;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using System.Collections.Concurrent;

namespace MareSynchronosServer.Services;

public class GrpcClientIdentificationService : GrpcBaseService, IClientIdentificationService
{
    private readonly string _shardName;
    private readonly ILogger<GrpcClientIdentificationService> _logger;
    private readonly IdentificationService.IdentificationServiceClient _grpcIdentClient;
    private readonly IdentificationService.IdentificationServiceClient _grpcIdentClientStreamOut;
    private readonly IdentificationService.IdentificationServiceClient _grpcIdentClientStreamIn;
    private readonly MareMetrics _metrics;
    protected readonly ConcurrentDictionary<string, UidWithIdent> OnlineClients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UidWithIdent> RemoteCachedIdents = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<IdentChange> _identChangeQueue = new();

    public GrpcClientIdentificationService(ILogger<GrpcClientIdentificationService> logger,
        IdentificationService.IdentificationServiceClient gprcIdentClient,
        IdentificationService.IdentificationServiceClient gprcIdentClientStreamOut,
        IdentificationService.IdentificationServiceClient gprcIdentClientStreamIn, 
        MareMetrics metrics, IConfigurationService<ServerConfiguration> configuration) : base(logger)
    {
        _shardName = configuration.GetValueOrDefault(nameof(ServerConfiguration.ShardName), string.Empty);
        _logger = logger;
        _grpcIdentClient = gprcIdentClient;
        _grpcIdentClientStreamOut = gprcIdentClientStreamOut;
        _grpcIdentClientStreamIn = gprcIdentClientStreamIn;
        _metrics = metrics;
    }

    public bool IsOnCurrentServer(string uid)
    {
        return OnlineClients.ContainsKey(uid);
    }

    public string? GetCharacterIdentForUid(string uid)
    {
        if (OnlineClients.TryGetValue(uid, out var ident))
        {
            return ident.Ident.Ident;
        }

        if (RemoteCachedIdents.TryGetValue(uid, out var cachedIdent))
        {
            return cachedIdent.Ident.Ident;
        }

        return null;
    }

    public string? GetServerForUid(string uid)
    {
        if (OnlineClients.ContainsKey(uid))
        {
            return _shardName;
        }

        if (RemoteCachedIdents.TryGetValue(uid, out var cachedIdent))
        {
            return cachedIdent.Ident.ServerId;
        }

        return null;
    }

    public async Task<long> GetOnlineUsers()
    {
        var result = await InvokeOnGrpc(_grpcIdentClient.GetOnlineUserCountAsync(new ServerMessage())).ConfigureAwait(false);
        if (result == default(OnlineUserCountResponse)) return OnlineClients.Count;
        return result.Count;
    }

    public string? GetUidForCharacterIdent(string characterIdent)
    {
        bool existsLocal = OnlineClients.Any(o => string.Equals(o.Value.Ident.Ident, characterIdent, StringComparison.Ordinal));
        if (existsLocal)
        {
            return OnlineClients.First(c => string.Equals(c.Value.Ident.Ident, characterIdent, StringComparison.Ordinal)).Key;
        }

        bool existsCached = RemoteCachedIdents.Any(o => string.Equals(o.Value.Ident.Ident, characterIdent, StringComparison.Ordinal));
        if (existsCached)
        {
            return RemoteCachedIdents.First(c => string.Equals(c.Value.Ident.Ident, characterIdent, StringComparison.Ordinal)).Key;
        }

        return null;
    }

    public void MarkUserOffline(string uid)
    {
        if (OnlineClients.TryRemove(uid, out var uidWithIdent))
        {
            _metrics.SetGaugeTo(MetricsAPI.GaugeAuthorizedConnections, OnlineClients.Count);
            _identChangeQueue.Enqueue(new IdentChange()
            {
                IsOnline = false,
                UidWithIdent = uidWithIdent
            });
        }
    }

    public void MarkUserOnline(string uid, string charaIdent)
    {
        OnlineClients[uid] = new UidWithIdent()
        {
            Uid = new()
            {
                Uid = uid,
            },
            Ident = new()
            {
                Ident = charaIdent,
                ServerId = _shardName
            }
        };

        _metrics.SetGaugeTo(MetricsAPI.GaugeAuthorizedConnections, OnlineClients.Count);
        _identChangeQueue.Enqueue(new IdentChange()
        {
            IsOnline = true,
            UidWithIdent = OnlineClients[uid]
        });
    }

    private async Task StreamOnlineClientData(CancellationToken cts)
    {
        try
        {
            using var stream = _grpcIdentClientStreamOut.SendStreamIdentStatusChange(cancellationToken: cts);
            _logger.LogInformation("Starting Send Online Client Data stream");
            await stream.RequestStream.WriteAsync(new IdentChangeMessage()
            {
                Server = new ServerMessage()
                {
                    ServerId = _shardName
                }
            }, cts).ConfigureAwait(false);

            while (!cts.IsCancellationRequested)
            {
                if (_identChangeQueue.TryDequeue(out var result))
                {
                    await stream.RequestStream.WriteAsync(new() { IdentChange = result }, cts).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(10, cts).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { return; }
        catch
        {
            SetGrpcFaulty();
        }
    }

    private async Task ReceiveOnlineClientData(CancellationToken cts)
    {
        try
        {
            using var stream = _grpcIdentClientStreamIn.ReceiveStreamIdentStatusChange(new ServerMessage()
            {
                ServerId = _shardName,
            }, cancellationToken: cts);
            _logger.LogInformation("Starting Receive Online Client Data stream");
            await foreach (var cur in stream.ResponseStream.ReadAllAsync(cts).ConfigureAwait(false))
            {
                if (cur.IsOnline)
                {
                    RemoteCachedIdents[cur.UidWithIdent.Uid.Uid] = cur.UidWithIdent;
                }
                else if (RemoteCachedIdents.TryGetValue(cur.UidWithIdent.Uid.Uid, out var existingIdent)
                    && string.Equals(existingIdent.Ident.ServerId, cur.UidWithIdent.Ident.ServerId, StringComparison.Ordinal))
                {
                    RemoteCachedIdents.TryRemove(cur.UidWithIdent.Uid.Uid, out _);
                }
            }
            _logger.LogCritical("Receive Online Client Data Stream ended");
        }
        catch (OperationCanceledException) { return; }
        catch
        {
            SetGrpcFaulty();
        }
    }

    protected override Task StartAsyncInternal(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected override async Task StopAsyncInternal(CancellationToken cancellationToken)
    {
        await ExecuteOnGrpc(_grpcIdentClient.ClearIdentsForServerAsync(new ServerMessage() { ServerId = _shardName }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    protected override async Task OnGrpcRestore()
    {
        var msg = new ServerIdentMessage();
        msg.Idents.AddRange(OnlineClients.Select(c => new SetIdentMessage()
        {
            UidWithIdent = c.Value
        }));
        await _grpcIdentClient.RecreateServerIdentsAsync(msg).ConfigureAwait(false);
    }

    protected override async Task PreStartStream()
    {
        await _grpcIdentClient.ClearIdentsForServerAsync(new ServerMessage() { ServerId = _shardName }).ConfigureAwait(false);

        RemoteCachedIdents.Clear();
    }

    protected override Task StartStream(CancellationToken ct)
    {
        _ = StreamOnlineClientData(ct);
        _ = ReceiveOnlineClientData(ct);
        return Task.CompletedTask;
    }

    protected override async Task PostStartStream()
    {
        var remoteOnlineClients = await _grpcIdentClient.GetAllIdentsAsync(new ServerMessage()
        {
            ServerId = _shardName
        }).ConfigureAwait(false);
        foreach (var result in remoteOnlineClients.UidWithIdent)
        {
            RemoteCachedIdents[result.Uid.Uid] = result;
        }
    }
}
