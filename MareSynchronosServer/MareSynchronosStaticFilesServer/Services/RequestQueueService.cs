﻿using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosStaticFilesServer.Utils;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Timers;
using MareSynchronos.API.SignalR;
using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;
using System.Linq;

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
    private readonly ConcurrentDictionary<Guid, string> _queueRemoval = new();
    private readonly SemaphoreSlim _queueSemaphore = new(1);
    private readonly UserQueueEntry[] _userQueueRequests;
    private readonly ConcurrentDictionary<string, PriorityEntry> _priorityCache = new(StringComparer.Ordinal);
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

    private async Task<bool> IsHighPriority(string uid, MareDbContext mareDbContext)
    {
        if (!_priorityCache.TryGetValue(uid, out PriorityEntry entry) || entry.LastChecked.Add(TimeSpan.FromHours(6)) < DateTime.UtcNow)
        {
            var user = await mareDbContext.Users.FirstOrDefaultAsync(u => u.UID == uid).ConfigureAwait(false);
            entry = new(user != null && !string.IsNullOrEmpty(user.Alias), DateTime.UtcNow);
            _priorityCache[uid] = entry;
        }

        return entry.IsHighPriority;
    }

    public async Task EnqueueUser(UserRequest request, MareDbContext mareDbContext)
    {
        _logger.LogDebug("Enqueueing req {guid} from {user} for {file}", request.RequestId, request.User, string.Join(", ", request.FileIds));

        bool isPriorityQueue = await IsHighPriority(request.User, mareDbContext).ConfigureAwait(false);

        if (_queueProcessingSemaphore.CurrentCount == 0)
        {
            if (isPriorityQueue) _priorityQueue.Enqueue(request);
            else _queue.Enqueue(request);
            return;
        }

        try
        {
            await _queueSemaphore.WaitAsync().ConfigureAwait(false);
            if (isPriorityQueue) _priorityQueue.Enqueue(request);
            else _queue.Enqueue(request);

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

    public async Task<bool> StillEnqueued(Guid request, string user, MareDbContext mareDbContext)
    {
        bool isPriorityQueue = await IsHighPriority(user, mareDbContext).ConfigureAwait(false);
        if (isPriorityQueue)
        {
            return _priorityQueue.Any(c => c.RequestId == request && string.Equals(c.User, user, StringComparison.Ordinal));
        }
        return _queue.Any(c => c.RequestId == request && string.Equals(c.User, user, StringComparison.Ordinal));
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
                try
                {
                    if (_userQueueRequests[i] != null && ((!_userQueueRequests[i].IsActive && _userQueueRequests[i].ExpirationDate < DateTime.UtcNow)))
                    {
                        _logger.LogDebug("Expiring inactive request {guid} slot {slot}", _userQueueRequests[i].UserRequest.RequestId, i);
                        _userQueueRequests[i] = null;
                    }

                    if (_userQueueRequests[i] != null && (_userQueueRequests[i].IsActive && _userQueueRequests[i].ActivationDate < DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(_queueReleaseSeconds))))
                    {
                        _logger.LogDebug("Expiring active request {guid} slot {slot}", _userQueueRequests[i].UserRequest.RequestId, i);
                        _userQueueRequests[i] = null;
                    }

                    if (!_queue.Any() && !_priorityQueue.Any()) return;

                    if (_userQueueRequests[i] == null)
                    {
                        bool enqueued = false;
                        while (!enqueued)
                        {
                            if (_priorityQueue.TryDequeue(out var prioRequest))
                            {
                                if (_queueRemoval.TryGetValue(prioRequest.RequestId, out string user) && string.Equals(user, prioRequest.User, StringComparison.Ordinal))
                                {
                                    _logger.LogDebug("Request cancelled: {requestId} by {user}", prioRequest.RequestId, user);
                                    _queueRemoval.Remove(prioRequest.RequestId, out _);
                                    continue;
                                }

                                await DequeueIntoSlotAsync(prioRequest, i).ConfigureAwait(false);
                                enqueued = true;
                                break;
                            }

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
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during inside queue processing");
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

        _metrics.SetGaugeTo(MetricsAPI.GaugeQueueFree, _userQueueRequests.Count(c => c == null));
        _metrics.SetGaugeTo(MetricsAPI.GaugeQueueActive, _userQueueRequests.Count(c => c != null && c.IsActive));
        _metrics.SetGaugeTo(MetricsAPI.GaugeQueueInactive, _userQueueRequests.Count(c => c != null && !c.IsActive));
        _metrics.SetGaugeTo(MetricsAPI.GaugeDownloadQueue, _queue.Count);
    }
}