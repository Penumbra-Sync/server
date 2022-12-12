using System.Collections.Concurrent;
using MareSynchronosShared.Protos;
using Microsoft.Extensions.Logging;

namespace MareSynchronosShared.Services;

public class GrpcAuthenticationService : GrpcBaseService
{
    private record AuthRequestInternal
    {
        public AuthRequest Request { get; set; }
        public long Id { get; set; }
    }

    private record AuthResponseCache
    {
        public AuthReply Response { get; set; }
        public DateTime WrittenTo { get; set; }
    }

    private readonly AuthService.AuthServiceClient _authClient;
    private readonly ConcurrentQueue<AuthRequestInternal> _requestQueue = new();
    private readonly ConcurrentDictionary<long, AuthReply> _authReplies = new();
    private readonly ConcurrentDictionary<string, AuthResponseCache> _cachedPositiveResponses = new(StringComparer.Ordinal);
    private long _requestId = 0;

    public GrpcAuthenticationService(ILogger<GrpcAuthenticationService> logger, AuthService.AuthServiceClient authClient) : base(logger)
    {
        _authClient = authClient;
    }

    public async Task<AuthReply> AuthorizeAsync(string ip, string secretKey)
    {
        if (_cachedPositiveResponses.TryGetValue(secretKey, out var cachedPositiveResponse))
        {
            if (cachedPositiveResponse.WrittenTo.AddMinutes(5) < DateTime.UtcNow) return cachedPositiveResponse.Response;
            _cachedPositiveResponses.Remove(secretKey, out _);
        }

        var id = Interlocked.Increment(ref _requestId);
        _requestQueue.Enqueue(new AuthRequestInternal()
        {
            Id = id,
            Request = new AuthRequest()
            {
                Ip = ip,
                SecretKey = secretKey,
            }
        });

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        AuthReply response = null;

        while (!GrpcIsFaulty && !cts.IsCancellationRequested && !_authReplies.TryRemove(id, out response))
        {
            await Task.Delay(10, cts.Token).ConfigureAwait(false);
        }

        if (response?.Success ?? false)
        {
            _cachedPositiveResponses[secretKey] = new AuthResponseCache
            {
                Response = response,
                WrittenTo = DateTime.UtcNow
            };
        }

        return response ?? new AuthReply
        {
            Success = false,
        };
    }

    public async Task GrpcAuthStream(CancellationToken token)
    {
        try
        {
            using var stream = _authClient.Authorize(cancellationToken: token);
            while (!token.IsCancellationRequested)
            {
                while (_requestQueue.TryDequeue(out var request))
                {
                    await stream.RequestStream.WriteAsync(request.Request, token).ConfigureAwait(false);
                    await stream.ResponseStream.MoveNext(token).ConfigureAwait(false);
                    _authReplies[request.Id] = stream.ResponseStream.Current;
                }

                await Task.Delay(10, token).ConfigureAwait(false);
            }
        }
        catch
        {
            SetGrpcFaulty();
        }
    }

    protected override Task OnGrpcRestore()
    {
        return Task.CompletedTask;
    }

    protected override Task PostStartStream()
    {
        return Task.CompletedTask;
    }

    protected override Task PreStartStream()
    {
        _requestQueue.Clear();
        _authReplies.Clear();
        return Task.CompletedTask;
    }

    protected override Task StartAsyncInternal(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected override Task StartStream(CancellationToken ct)
    {
        _ = GrpcAuthStream(ct);
        return Task.CompletedTask;
    }

    protected override Task StopAsyncInternal(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
