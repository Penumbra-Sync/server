using MareSynchronosShared.Services;
using MareSynchronosStaticFilesServer.Utils;
using System.Collections.Concurrent;

namespace MareSynchronosStaticFilesServer.Services;

public class RequestQueueService : IHostedService
{
    private CancellationTokenSource _queueCts = new();
    private readonly UserQueueEntry[] _userQueueRequests;
    private readonly ConcurrentQueue<UserRequest> _queue = new();
    private readonly ILogger<RequestQueueService> _logger;
    private readonly int _queueExpirationSeconds;

    public RequestQueueService(IConfigurationService<StaticFilesServerConfiguration> configurationService, ILogger<RequestQueueService> logger)
    {
        _userQueueRequests = new UserQueueEntry[configurationService.GetValueOrDefault(nameof(StaticFilesServerConfiguration.DownloadQueueSize), 50)];
        _queueExpirationSeconds = configurationService.GetValueOrDefault(nameof(StaticFilesServerConfiguration.DownloadTimeoutSeconds), 5);
        _logger = logger;
    }

    public void EnqueueUser(UserRequest request)
    {
        _logger.LogInformation("Enqueueing req {guid} from {user} for {file}", request.RequestId, request.User, request.FileId);
        _queue.Enqueue(request);
    }

    public bool StillEnqueued(Guid request, string user)
    {
        return _queue.Any(c=>c.RequestId == request && string.Equals(c.User, user, StringComparison.Ordinal));
    }

    public bool IsActiveProcessing(Guid request, string user, out UserRequest userRequest)
    {
        var userQueueRequest = _userQueueRequests.Where(u => u != null)
            .FirstOrDefault(f => f.UserRequest.RequestId == request && string.Equals(f.UserRequest.User, user, StringComparison.Ordinal));
        userRequest = userQueueRequest?.UserRequest ?? null;
        return userQueueRequest != null && userRequest != null && userQueueRequest.ExpirationDate > DateTime.UtcNow;
    }

    public void FinishRequest(Guid request)
    {
        var req = _userQueueRequests.First(f => f.UserRequest.RequestId == request);
        var idx = Array.IndexOf(_userQueueRequests, req);
        _logger.LogDebug("Finishing Request {guid}, clearing slot {idx}", request, idx);
        _userQueueRequests[idx] = null;
    }

    public void ActivateRequest(Guid request)
    {
        _logger.LogDebug("Activating request {guid}", request);
        _userQueueRequests.First(f => f.UserRequest.RequestId == request).IsActive = true;
    }

    private async Task ProcessRequestQueue(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_queue.Any()) { await Task.Delay(100).ConfigureAwait(false); continue; }
            for (int i = 0; i < _userQueueRequests.Length; i++)
            {
                if (_userQueueRequests[i] != null && !_userQueueRequests[i].IsActive && _userQueueRequests[i].ExpirationDate < DateTime.UtcNow) _userQueueRequests[i] = null;

                if (_userQueueRequests[i] == null)
                {
                    if (_queue.TryDequeue(out var request))
                    {
                        _logger.LogDebug("Dequeueing {req} into {i}: {user} with {file}", request.RequestId, i, request.User, request.FileId);
                        _userQueueRequests[i] = new(request, DateTime.UtcNow.AddSeconds(_queueExpirationSeconds));
                    }
                }

                if (!_queue.Any()) break;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = ProcessRequestQueue(_queueCts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _queueCts.Cancel();
        return Task.CompletedTask;
    }
}
