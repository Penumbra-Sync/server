using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosStaticFilesServer.Utils;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Timers;
using MareSynchronos.API.SignalR;

namespace MareSynchronosStaticFilesServer.Services;

public class RequestQueueService : IHostedService
{
    private record PriorityEntry(bool IsHighPriority, DateTime LastChecked);

    private readonly IHubContext<MareSynchronosServer.Hubs.MareHub> _hubContext;
    private readonly ILogger<RequestQueueService> _logger;
    private readonly MareMetrics _metrics;
    private readonly ConcurrentQueue<UserRequest> _queue = new();
    private readonly ConcurrentQueue<UserRequest> _priorityQueue = new();
    private readonly int _queueExpirationSeconds;
    private readonly SemaphoreSlim _queueProcessingSemaphore = new(1);
    private readonly SemaphoreSlim _queueSemaphore = new(1);
    private readonly UserQueueEntry[] _userQueueRequests;
    private int _queueLimitForReset;
    private readonly int _queueReleaseSeconds;
    private System.Timers.Timer _queueTimer;

    public RequestQueueService(MareMetrics metrics, IConfigurationService<StaticFilesServerConfiguration> configurationService,
        ILogger<RequestQueueService> logger, IHubContext<MareSynchronosServer.Hubs.MareHub> hubContext)
    {
        _userQueueRequests = new UserQueueEntry[configurationService.GetValueOrDefault(nameof(StaticFilesServerConfiguration.DownloadQueueSize), 50)];
        _queueExpirationSeconds = configurationService.GetValueOrDefault(nameof(StaticFilesServerConfiguration.DownloadTimeoutSeconds), 5);
        _queueLimitForReset = configurationService.GetValueOrDefault(nameof(StaticFilesServerConfiguration.DownloadQueueClearLimit), 15000);
        _queueReleaseSeconds = configurationService.GetValueOrDefault(nameof(StaticFilesServerConfiguration.DownloadQueueReleaseSeconds), 15);
        _metrics = metrics;
        _logger = logger;
        _hubContext = hubContext;
    }

    public void ActivateRequest(Guid request)
    {
        _logger.LogDebug("Activating request {guid}", request);
        var req = _userQueueRequests.First(f => f != null && f.UserRequest.RequestId == request);
        req.MarkActive();
    }

    public void EnqueueUser(UserRequest request, bool isPriority)
    {
        _logger.LogDebug("Enqueueing req {guid} from {user} for {file}", request.RequestId, request.User, string.Join(", ", request.FileIds));

        GetQueue(isPriority).Enqueue(request);
    }

    public void FinishRequest(Guid request)
    {
        var req = _userQueueRequests.FirstOrDefault(f => f != null && f.UserRequest.RequestId == request);
        if (req != null)
        {
            var idx = Array.IndexOf(_userQueueRequests, req);
            _logger.LogDebug("Finishing Request {guid}, clearing slot {idx}", request, idx);
            _userQueueRequests[idx] = null;
        }
        else
        {
            _logger.LogDebug("Request {guid} already cleared", request);
        }
    }

    public bool IsActiveProcessing(Guid request, string user, out UserRequest userRequest)
    {
        var userQueueRequest = _userQueueRequests.FirstOrDefault(u => u != null && u.UserRequest.RequestId == request && string.Equals(u.UserRequest.User, user, StringComparison.Ordinal));
        userRequest = userQueueRequest?.UserRequest ?? null;
        return userQueueRequest != null && userRequest != null && userQueueRequest.ExpirationDate > DateTime.UtcNow;
    }

    public void RemoveFromQueue(Guid requestId, string user, bool isPriority)
    {
        var existingRequest = GetQueue(isPriority).FirstOrDefault(f => f.RequestId == requestId && string.Equals(f.User, user, StringComparison.Ordinal));
        if (existingRequest == null)
        {
            var activeSlot = _userQueueRequests.FirstOrDefault(r => r != null && string.Equals(r.UserRequest.User, user, StringComparison.Ordinal) && r.UserRequest.RequestId == requestId);
            if (activeSlot != null)
            {
                var idx = Array.IndexOf(_userQueueRequests, activeSlot);
                if (idx >= 0)
                {
                    _userQueueRequests[idx] = null;
                }
            }
        }
        else
        {
            existingRequest.IsCancelled = true;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _queueTimer = new System.Timers.Timer(250);
        _queueTimer.Elapsed += ProcessQueue;
        _queueTimer.AutoReset = true;
        _queueTimer.Start();
        return Task.CompletedTask;
    }

    private ConcurrentQueue<UserRequest> GetQueue(bool isPriority) => isPriority ? _priorityQueue : _queue;

    public bool StillEnqueued(Guid request, string user, bool isPriority)
    {
        return GetQueue(isPriority).Any(c => c.RequestId == request && string.Equals(c.User, user, StringComparison.Ordinal));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _queueTimer.Stop();
        return Task.CompletedTask;
    }

    private async Task DequeueIntoSlotAsync(UserRequest userRequest, int slot)
    {
        _logger.LogDebug("Dequeueing {req} into {i}: {user} with {file}", userRequest.RequestId, slot, userRequest.User, string.Join(", ", userRequest.FileIds));
        _userQueueRequests[slot] = new(userRequest, DateTime.UtcNow.AddSeconds(_queueExpirationSeconds));
        await _hubContext.Clients.User(userRequest.User).SendAsync(nameof(IMareHub.Client_DownloadReady), userRequest.RequestId).ConfigureAwait(false);
    }

    private async void ProcessQueue(object src, ElapsedEventArgs e)
    {
        _logger.LogDebug("Periodic Processing Queue Start");
        _metrics.SetGaugeTo(MetricsAPI.GaugeQueueFree, _userQueueRequests.Count(c => c == null));
        _metrics.SetGaugeTo(MetricsAPI.GaugeQueueActive, _userQueueRequests.Count(c => c != null && c.IsActive));
        _metrics.SetGaugeTo(MetricsAPI.GaugeQueueInactive, _userQueueRequests.Count(c => c != null && !c.IsActive));
        _metrics.SetGaugeTo(MetricsAPI.GaugeDownloadQueue, _queue.Count(q => !q.IsCancelled));
        _metrics.SetGaugeTo(MetricsAPI.GaugeDownloadQueueCancelled, _queue.Count(q => q.IsCancelled));
        _metrics.SetGaugeTo(MetricsAPI.GaugeDownloadPriorityQueue, _priorityQueue.Count(q => !q.IsCancelled));
        _metrics.SetGaugeTo(MetricsAPI.GaugeDownloadPriorityQueueCancelled, _priorityQueue.Count(q => q.IsCancelled));

        if (_queueProcessingSemaphore.CurrentCount == 0)
        {
            _logger.LogDebug("Aborting Periodic Processing Queue, processing still running");
            return;
        }

        await _queueProcessingSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_queue.Count(c => !c.IsCancelled) > _queueLimitForReset)
            {
                _queue.Clear();
                return;
            }

            for (int i = 0; i < _userQueueRequests.Length; i++)
            {
                try
                {
                    if (_userQueueRequests[i] != null
                        && (((!_userQueueRequests[i].IsActive && _userQueueRequests[i].ExpirationDate < DateTime.UtcNow))
                            || (_userQueueRequests[i].IsActive && _userQueueRequests[i].ActivationDate < DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(_queueReleaseSeconds))))
                            )
                    {
                        _logger.LogDebug("Expiring request {guid} slot {slot}", _userQueueRequests[i].UserRequest.RequestId, i);
                        _userQueueRequests[i] = null;
                    }

                    if (!_queue.Any() && !_priorityQueue.Any()) break;

                    while (true)
                    {
                        if (_priorityQueue.TryDequeue(out var prioRequest))
                        {
                            if (prioRequest.IsCancelled) continue;

                            await DequeueIntoSlotAsync(prioRequest, i).ConfigureAwait(false);
                            break;
                        }

                        if (_queue.TryDequeue(out var request))
                        {
                            if (request.IsCancelled) continue;

                            await DequeueIntoSlotAsync(request, i).ConfigureAwait(false);
                            break;
                        }

                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during inside queue processing");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Queue processing");
        }
        finally
        {
            _queueProcessingSemaphore.Release();
            _logger.LogDebug("Periodic Processing Queue End");
        }
    }
}