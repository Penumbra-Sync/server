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
    private readonly IHubContext<MareSynchronosServer.Hubs.MareHub> _hubContext;
    private readonly ILogger<RequestQueueService> _logger;
    private readonly MareMetrics _metrics;
    private readonly ConcurrentQueue<UserRequest> _queue = new();
    private readonly int _queueExpirationSeconds;
    private readonly SemaphoreSlim _queueProcessingSemaphore = new(1);
    private readonly ConcurrentDictionary<Guid, string> _queueRemoval = new();
    private readonly SemaphoreSlim _queueSemaphore = new(1);
    private readonly UserQueueEntry[] _userQueueRequests;
    private int _queueLimitForReset;
    private System.Timers.Timer _queueTimer;

    public RequestQueueService(MareMetrics metrics, IConfigurationService<StaticFilesServerConfiguration> configurationService, ILogger<RequestQueueService> logger, IHubContext<MareSynchronosServer.Hubs.MareHub> hubContext)
    {
        _userQueueRequests = new UserQueueEntry[configurationService.GetValueOrDefault(nameof(StaticFilesServerConfiguration.DownloadQueueSize), 50)];
        _queueExpirationSeconds = configurationService.GetValueOrDefault(nameof(StaticFilesServerConfiguration.DownloadTimeoutSeconds), 5);
        _queueLimitForReset = configurationService.GetValueOrDefault(nameof(StaticFilesServerConfiguration.DownloadQueueClearLimit), 15000);
        _metrics = metrics;
        _logger = logger;
        _hubContext = hubContext;
    }

    public void ActivateRequest(Guid request)
    {
        _logger.LogDebug("Activating request {guid}", request);
        _userQueueRequests.First(f => f != null && f.UserRequest.RequestId == request).IsActive = true;
    }

    public async Task EnqueueUser(UserRequest request)
    {
        _logger.LogDebug("Enqueueing req {guid} from {user} for {file}", request.RequestId, request.User, request.FileId);

        if (_queueProcessingSemaphore.CurrentCount == 0)
        {
            _queue.Enqueue(request);
            return;
        }

        try
        {
            await _queueSemaphore.WaitAsync().ConfigureAwait(false);
            _queue.Enqueue(request);

            return;
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

    public void FinishRequest(Guid request)
    {
        var req = _userQueueRequests.First(f => f != null && f.UserRequest.RequestId == request);
        var idx = Array.IndexOf(_userQueueRequests, req);
        _logger.LogDebug("Finishing Request {guid}, clearing slot {idx}", request, idx);
        _userQueueRequests[idx] = null;
    }

    public bool IsActiveProcessing(Guid request, string user, out UserRequest userRequest)
    {
        var userQueueRequest = _userQueueRequests.FirstOrDefault(u => u != null && u.UserRequest.RequestId == request && string.Equals(u.UserRequest.User, user, StringComparison.Ordinal));
        userRequest = userQueueRequest?.UserRequest ?? null;
        return userQueueRequest != null && userRequest != null && userQueueRequest.ExpirationDate > DateTime.UtcNow;
    }

    public void RemoveFromQueue(Guid requestId, string user)
    {
        if (!_queue.Any(f => f.RequestId == requestId && string.Equals(f.User, user, StringComparison.Ordinal)))
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

            return;
        }
        _queueRemoval[requestId] = user;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _queueTimer = new System.Timers.Timer(250);
        _queueTimer.Elapsed += ProcessQueue;
        _queueTimer.AutoReset = true;
        _queueTimer.Start();
        return Task.CompletedTask;
    }

    public bool StillEnqueued(Guid request, string user)
    {
        return _queue.Any(c => c.RequestId == request && string.Equals(c.User, user, StringComparison.Ordinal));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _queueTimer.Stop();
        return Task.CompletedTask;
    }

    private async Task DequeueIntoSlotAsync(UserRequest userRequest, int slot)
    {
        _logger.LogDebug("Dequeueing {req} into {i}: {user} with {file}", userRequest.RequestId, slot, userRequest.User, userRequest.FileId);
        _userQueueRequests[slot] = new(userRequest, DateTime.UtcNow.AddSeconds(_queueExpirationSeconds));
        await _hubContext.Clients.User(userRequest.User).SendAsync(nameof(IMareHub.Client_DownloadReady), userRequest.RequestId).ConfigureAwait(false);
    }

    private async void ProcessQueue(object src, ElapsedEventArgs e)
    {
        if (_queueProcessingSemaphore.CurrentCount == 0) return;

        await _queueProcessingSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_queue.Count > _queueLimitForReset)
            {
                _queue.Clear();
                return;
            }

            Parallel.For(0, _userQueueRequests.Length, new ParallelOptions()
            {
                MaxDegreeOfParallelism = 10,
            },
            async (i) =>
            {
                if (!_queue.Any()) return;

                if (_userQueueRequests[i] != null && !_userQueueRequests[i].IsActive && _userQueueRequests[i].ExpirationDate < DateTime.UtcNow) _userQueueRequests[i] = null;

                if (_userQueueRequests[i] == null)
                {
                    bool enqueued = false;
                    while (!enqueued)
                    {
                        if (_queue.TryDequeue(out var request))
                        {
                            if (_queueRemoval.TryGetValue(request.RequestId, out string user) && string.Equals(user, request.User, StringComparison.Ordinal))
                            {
                                _logger.LogDebug("Request cancelled: {requestId} by {user}", request.RequestId, user);
                                _queueRemoval.Remove(request.RequestId, out _);
                                continue;
                            }

                            await DequeueIntoSlotAsync(request, i).ConfigureAwait(false);
                            enqueued = true;
                        }
                        else
                        {
                            enqueued = true;
                        }
                    }
                }
            });
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
}