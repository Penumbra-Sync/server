using MareSynchronos.API;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosStaticFilesServer.Utils;
using System.Collections.Concurrent;
using System.Timers;

namespace MareSynchronosStaticFilesServer.Services;

public class RequestQueueService : IHostedService
{
    private readonly UserQueueEntry[] _userQueueRequests;
    private readonly ConcurrentQueue<UserRequest> _queue = new();
    private readonly MareMetrics _metrics;
    private readonly ILogger<RequestQueueService> _logger;
    private readonly int _queueExpirationSeconds;
    private SemaphoreSlim _queueSemaphore = new(1);
    private SemaphoreSlim _queueProcessingSemaphore = new(1);
    private System.Timers.Timer _queueTimer;

    public RequestQueueService(MareMetrics metrics, IConfigurationService<StaticFilesServerConfiguration> configurationService, ILogger<RequestQueueService> logger)
    {
        _userQueueRequests = new UserQueueEntry[configurationService.GetValueOrDefault(nameof(StaticFilesServerConfiguration.DownloadQueueSize), 50)];
        _queueExpirationSeconds = configurationService.GetValueOrDefault(nameof(StaticFilesServerConfiguration.DownloadTimeoutSeconds), 5);
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<QueueStatus> EnqueueUser(UserRequest request)
    {
        _logger.LogDebug("Enqueueing req {guid} from {user} for {file}", request.RequestId, request.User, request.FileId);

        if (_queueProcessingSemaphore.CurrentCount == 0)
        {
            _queue.Enqueue(request);
            return QueueStatus.Waiting;
        }

        try
        {
            await _queueSemaphore.WaitAsync().ConfigureAwait(false);
            QueueStatus status = QueueStatus.Waiting;
            var idx = Array.FindIndex(_userQueueRequests, r => r == null);
            if (idx == -1)
            {
                _queue.Enqueue(request);
                status = QueueStatus.Waiting;
            }
            else
            {
                DequeueIntoSlot(request, idx);
                status = QueueStatus.Ready;
            }

            return status;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during EnqueueUser");
        }
        finally
        {
            _queueSemaphore.Release();
        }

        throw new Exception("Error during EnqueueUser");
    }

    public bool StillEnqueued(Guid request, string user, out int queuePosition)
    {
        var result = _queue.FirstOrDefault(c => c.RequestId == request && string.Equals(c.User, user, StringComparison.Ordinal));
        if (result != null)
        {
            queuePosition = Array.IndexOf(_queue.ToArray(), result);
            return true;
        }

        queuePosition = -1;
        return false;
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
        var req = _userQueueRequests.Where(f => f != null).First(f => f.UserRequest.RequestId == request);
        var idx = Array.IndexOf(_userQueueRequests, req);
        _logger.LogDebug("Finishing Request {guid}, clearing slot {idx}", request, idx);
        _userQueueRequests[idx] = null;
    }

    public void ActivateRequest(Guid request)
    {
        _logger.LogDebug("Activating request {guid}", request);
        _userQueueRequests.Where(f => f != null).First(f => f.UserRequest.RequestId == request).IsActive = true;
    }

    private async void ProcessQueue(object src, ElapsedEventArgs e)
    {
        if (_queueProcessingSemaphore.CurrentCount == 0) return;

        await _queueProcessingSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            for (int i = 0; i < _userQueueRequests.Length; i++)
            {
                if (_userQueueRequests[i] != null && !_userQueueRequests[i].IsActive && _userQueueRequests[i].ExpirationDate < DateTime.UtcNow) _userQueueRequests[i] = null;

                if (_userQueueRequests[i] == null)
                {
                    if (_queue.TryDequeue(out var request))
                    {
                        DequeueIntoSlot(request, i);
                    }
                }

                if (!_queue.Any()) break;
            }

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Queue processing");
        }
        finally
        {
            _queueProcessingSemaphore.Release();
        }

        _metrics.SetGaugeTo(MetricsAPI.GaugeDownloadQueue, _queue.Count);
    }

    private void DequeueIntoSlot(UserRequest userRequest, int slot)
    {
        _logger.LogDebug("Dequeueing {req} into {i}: {user} with {file}", userRequest.RequestId, slot, userRequest.User, userRequest.FileId);
        _userQueueRequests[slot] = new(userRequest, DateTime.UtcNow.AddSeconds(_queueExpirationSeconds));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _queueTimer = new System.Timers.Timer(250);
        _queueTimer.Elapsed += ProcessQueue;
        _queueTimer.AutoReset = true;
        _queueTimer.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _queueTimer.Stop();
        return Task.CompletedTask;
    }
}
