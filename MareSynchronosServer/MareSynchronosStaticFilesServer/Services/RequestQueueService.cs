using MareSynchronosShared.Services;
using System.Collections.Concurrent;

namespace MareSynchronosStaticFilesServer.Services;

public record UserQueueEntry(UserRequest UserRequest, DateTime ExpirationDate)
{
    public bool IsActive { get; set; } = false;
}

public record UserRequest(Guid RequestId, string User, string FileId);


public class RequestQueueService : IHostedService
{
    private CancellationTokenSource _queueCts = new();
    private readonly UserQueueEntry[] _userQueueRequests;
    private readonly ConcurrentQueue<UserRequest> _queue = new();
    private readonly ILogger<RequestQueueService> _logger;

    public RequestQueueService(IConfigurationService<StaticFilesServerConfiguration> configurationService, ILogger<RequestQueueService> logger)
    {
        _userQueueRequests = new UserQueueEntry[configurationService.GetValueOrDefault(nameof(StaticFilesServerConfiguration.DownloadQueueSize), 50)];
        _logger = logger;
    }

    public void EnqueueUser(UserRequest request)
    {
        _logger.LogInformation("Enqueueing req {guid} from {user} for {file}", request.RequestId, request.User, request.FileId);
        _queue.Enqueue(request);
    }

    public bool IsActiveProcessing(Guid request, out UserRequest userRequest)
    {
        userRequest = _userQueueRequests.FirstOrDefault(f => f.UserRequest.RequestId == request)?.UserRequest ?? null;
        return userRequest != null;
    }

    public void FinishRequest(Guid request)
    {
        _userQueueRequests.First(f => f.UserRequest.RequestId == request).IsActive = false;
    }

    public void ActivateRequest(Guid request)
    {
        _userQueueRequests.First(f => f.UserRequest.RequestId == request).IsActive = true;
    }

    private async Task ProcessRequestQueue(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_queue.Any()) { await Task.Delay(100).ConfigureAwait(false); continue; }
            for (int i = 0; i < _userQueueRequests.Length; i++)
            {
                if (_userQueueRequests[i] == null || (!_userQueueRequests[i].IsActive && _userQueueRequests[i].ExpirationDate < DateTime.UtcNow))
                {
                    if (_queue.TryDequeue(out var request))
                    {
                        _logger.LogInformation("Dequeueing {req} into {i}: {user} with {file}", i, request.RequestId, request.User, request.FileId);
                        _userQueueRequests[i] = new(request, DateTime.Now.AddSeconds(5));
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
