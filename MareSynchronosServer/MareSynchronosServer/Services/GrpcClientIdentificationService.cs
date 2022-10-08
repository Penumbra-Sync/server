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
    private readonly MareMetrics _metrics;
    protected ConcurrentDictionary<string, string> OnlineClients = new(StringComparer.Ordinal);
    private ConcurrentDictionary<string, string> RemoteCachedIdents = new(StringComparer.Ordinal);
    private readonly TimeSpan InvalidateCachedIdent = TimeSpan.FromSeconds(30);
    private bool _grpcIsFaulty = false;
    private CancellationTokenSource _cacheVerificationCts = new();

    public GrpcClientIdentificationService(ILogger<GrpcClientIdentificationService> logger, IdentificationService.IdentificationServiceClient gprcIdentClient, MareMetrics metrics, IConfiguration configuration)
    {
        var config = configuration.GetSection("MareSynchronos");
        _shardName = config.GetValue("ShardName", "Main");
        _logger = logger;
        _grpcIdentClient = gprcIdentClient;
        _metrics = metrics;
    }

    private async Task HandleCacheVerification(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (RemoteCachedIdents.Any())
                {
                    MultiUidMessage req = new();
                    req.Uids.AddRange(RemoteCachedIdents.Select(k => new UidMessage() { Uid = k.Key }));
                    var cacheResponse = await InvokeOnGrpc(_grpcIdentClient.ValidateCachedIdentsAsync(req)).ConfigureAwait(false);

                    foreach (var entry in cacheResponse.UidWithIdent)
                    {
                        if (!RemoteCachedIdents.TryGetValue(entry.Uid.Uid, out var ident))
                        {
                            continue;
                        }

                        if (string.IsNullOrEmpty(entry.Ident.Ident))
                        {
                            RemoteCachedIdents.TryRemove(entry.Uid.Uid, out var _);
                            continue;
                        }

                        RemoteCachedIdents[entry.Uid.Uid] = entry.Ident.Ident;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during cached idents verification");
            }
            finally
            {
                await Task.Delay(InvalidateCachedIdent, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<string?> GetCharacterIdentForUid(string uid)
    {
        if (OnlineClients.TryGetValue(uid, out string ident))
        {
            return ident;
        }

        if (RemoteCachedIdents.TryGetValue(uid, out var cachedIdent))
        {
            return cachedIdent;
        }

        var result = await InvokeOnGrpc(_grpcIdentClient.GetIdentForUidAsync(new UidMessage { Uid = uid })).ConfigureAwait(false);
        if (result == default(CharacterIdentMessage)) return null;
        RemoteCachedIdents[uid] = result.Ident;
        return result.Ident;
    }

    public async Task<string?> GetServerForUid(string uid)
    {
        if (OnlineClients.ContainsKey(uid))
        {
            return _shardName;
        }

        var result = await InvokeOnGrpc(_grpcIdentClient.GetIdentForUidAsync(new UidMessage { Uid = uid })).ConfigureAwait(false);
        if (result == default(CharacterIdentMessage)) return null;
        return result.ServerId;
    }

    public async Task<long> GetOnlineUsers()
    {
        var result = await InvokeOnGrpc(_grpcIdentClient.GetOnlineUserCountAsync(new ServerMessage())).ConfigureAwait(false);
        if (result == default(OnlineUserCountResponse)) return OnlineClients.Count;
        return result.Count;
    }

    public async Task<string?> GetUidForCharacterIdent(string characterIdent)
    {
        bool existsLocal = OnlineClients.Any(o => string.Equals(o.Value, characterIdent, StringComparison.Ordinal));
        if (existsLocal)
        {
            return OnlineClients.First(c => string.Equals(c.Value, characterIdent, StringComparison.Ordinal)).Key;
        }

        var result = await InvokeOnGrpc(_grpcIdentClient.GetUidForCharacterIdentAsync(new CharacterIdentMessage { Ident = characterIdent, ServerId = string.Empty })).ConfigureAwait(false);
        if (result == default(UidMessage)) return null;
        return result.Uid;
    }

    public async Task MarkUserOffline(string uid)
    {
        OnlineClients.TryRemove(uid, out _);
        _metrics.SetGaugeTo(MetricsAPI.GaugeAuthorizedConnections, OnlineClients.Count);
        await ExecuteOnGrpc(_grpcIdentClient.RemoveIdentForUidAsync(new RemoveIdentMessage() { ServerId = _shardName, Uid = uid })).ConfigureAwait(false);
    }

    public async Task MarkUserOnline(string uid, string charaIdent)
    {
        OnlineClients[uid] = charaIdent;
        _metrics.SetGaugeTo(MetricsAPI.GaugeAuthorizedConnections, OnlineClients.Count);
        await ExecuteOnGrpc(_grpcIdentClient.SetIdentForUidAsync(new SetIdentMessage() { Ident = charaIdent, ServerId = _shardName, Uid = uid })).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ExecuteOnGrpc(_grpcIdentClient.ClearIdentsForServerAsync(new ServerMessage() { ServerId = _shardName })).ConfigureAwait(false);

        _ = HandleCacheVerification(_cacheVerificationCts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cacheVerificationCts.Cancel();
        await ExecuteOnGrpc(_grpcIdentClient.ClearIdentsForServerAsync(new ServerMessage() { ServerId = _shardName })).ConfigureAwait(false);
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
            _grpcIsFaulty = false;

            _logger.LogInformation("GRPC connection is restored, sending current server idents");
            await _grpcIdentClient.ClearIdentsForServerAsync(new ServerMessage() { ServerId = _shardName }).ConfigureAwait(false);
            var msg = new ServerIdentMessage();
            msg.Idents.AddRange(OnlineClients.Select(c => new SetIdentMessage()
            {
                Ident = c.Value,
                Uid = c.Key,
                ServerId = _shardName
            }));
            await _grpcIdentClient.RecreateServerIdentsAsync(msg).ConfigureAwait(false);
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