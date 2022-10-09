using Grpc.Core;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Protos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MareSynchronosServer.Services;

public class GrpcClientIdentificationService : IHostedService
{
    private readonly string _shardName;
    private readonly ILogger<GrpcClientIdentificationService> _logger;
    private readonly IdentificationService.IdentificationServiceClient _grpcIdentClient;
    private readonly IdentificationService.IdentificationServiceClient grpcIdentClientStreamOut;
    private readonly IdentificationService.IdentificationServiceClient grpcIdentClientStreamIn;
    private readonly MareMetrics _metrics;
    protected readonly ConcurrentDictionary<string, UidWithIdent> OnlineClients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UidWithIdent> RemoteCachedIdents = new(StringComparer.Ordinal);
    private bool _grpcIsFaulty = false;
    private ConcurrentQueue<IdentChange> _identChangeQueue = new();
    private CancellationTokenSource _streamCts = new();
    private CancellationTokenSource _faultCheckCts = new();

    public GrpcClientIdentificationService(ILogger<GrpcClientIdentificationService> logger, IdentificationService.IdentificationServiceClient gprcIdentClient, 
        IdentificationService.IdentificationServiceClient gprcIdentClientStreamOut, 
        IdentificationService.IdentificationServiceClient gprcIdentClientStreamIn, MareMetrics metrics, IConfiguration configuration)
    {
        var config = configuration.GetSection("MareSynchronos");
        _shardName = config.GetValue("ShardName", "Main");
        _logger = logger;
        _grpcIdentClient = gprcIdentClient;
        this.grpcIdentClientStreamOut = gprcIdentClientStreamOut;
        this.grpcIdentClientStreamIn = gprcIdentClientStreamIn;
        _metrics = metrics;
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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = RestartStreams();
        _ = CheckGrpcFaults(_faultCheckCts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _faultCheckCts.Cancel();
        _streamCts.Cancel();
        await ExecuteOnGrpc(_grpcIdentClient.ClearIdentsForServerAsync(new ServerMessage() { ServerId = _shardName })).ConfigureAwait(false);
    }

    private async Task CheckGrpcFaults(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckFaultStateAndResend().ConfigureAwait(false);
            }
            catch { SetGrpcFaulty(); }
            await Task.Delay(250).ConfigureAwait(false);
        }
    }

    private async Task RestartStreams()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = new();
        if (!_grpcIsFaulty)
        {
            try
            {
                await _grpcIdentClient.ClearIdentsForServerAsync(new ServerMessage() { ServerId = _shardName }).ConfigureAwait(false);

                RemoteCachedIdents.Clear();
                _ = StreamOnlineClientData(_streamCts.Token);
                _ = ReceiveOnlineClientData(_streamCts.Token);
                var remoteOnlineClients = await _grpcIdentClient.GetAllIdentsAsync(new ServerMessage()
                {
                    ServerId = _shardName
                }).ConfigureAwait(false);
                foreach (var result in remoteOnlineClients.UidWithIdent)
                {
                    RemoteCachedIdents[result.Uid.Uid] = result;
                }
            }
            catch
            {
                SetGrpcFaulty();
            }
        }
    }

    private async Task StreamOnlineClientData(CancellationToken cts)
    {
        try
        {
            using var stream = grpcIdentClientStreamOut.SendStreamIdentStatusChange(cancellationToken: cts);
            await stream.RequestStream.WriteAsync(new IdentChangeMessage()
            {
                Server = new ServerMessage()
                {
                    ServerId = _shardName
                }
            }).ConfigureAwait(false);

            while (!cts.IsCancellationRequested)
            {
                if (_identChangeQueue.TryDequeue(out var result))
                {
                    await stream.RequestStream.WriteAsync(new() { IdentChange = result }, cts).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(25, cts).ConfigureAwait(false);
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
            using var stream = grpcIdentClientStreamIn.ReceiveStreamIdentStatusChange(new ServerMessage()
            {
                ServerId = _shardName,
            });
            while (await stream.ResponseStream.MoveNext(cts).ConfigureAwait(false))
            {
                var cur = stream.ResponseStream.Current;
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

    private async Task<T> InvokeOnGrpc<T>(AsyncUnaryCall<T> toExecute)
    {
        try
        {
            var result = await toExecute.ConfigureAwait(false);

            await CheckFaultStateAndResend().ConfigureAwait(false);

            return result;
        }
        catch
        {
            SetGrpcFaulty();

            return default;
        }
    }

    private async Task ExecuteOnGrpc<T>(AsyncUnaryCall<T> toExecute)
    {
        try
        {
            await toExecute.ConfigureAwait(false);
            await CheckFaultStateAndResend().ConfigureAwait(false);
        }
        catch
        {
            SetGrpcFaulty();
        }
    }

    private async Task CheckFaultStateAndResend()
    {
        if (_grpcIsFaulty)
        {
            await RestartStreams().ConfigureAwait(false);
            var msg = new ServerIdentMessage();
            msg.Idents.AddRange(OnlineClients.Select(c => new SetIdentMessage()
            {
                UidWithIdent = c.Value
            }));
            await _grpcIdentClient.RecreateServerIdentsAsync(msg).ConfigureAwait(false);
            _logger.LogInformation("GRPC connection is restored");
            _grpcIsFaulty = false;
        }
    }

    private void SetGrpcFaulty()
    {
        if (!_grpcIsFaulty)
        {
            _grpcIsFaulty = true;
            _logger.LogWarning("GRPC connection is faulty");
        }
    }
}