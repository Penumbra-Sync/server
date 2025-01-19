using MareSynchronos.API.Dto.CharaData;
using MareSynchronos.API.SignalR;
using MareSynchronosServer.Hubs;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis.Extensions.Core.Abstractions;

namespace MareSynchronosServer.Services;

public sealed class GPoseLobbyDistributionService : IHostedService, IDisposable
{
    private CancellationTokenSource _runtimeCts = new();
    private readonly Dictionary<string, Dictionary<string, WorldData>> _lobbyWorldData = [];
    private readonly Dictionary<string, Dictionary<string, PoseData>> _lobbyPoseData = [];
    private readonly SemaphoreSlim _lobbyPoseDataModificationSemaphore = new(1, 1);
    private readonly SemaphoreSlim _lobbyWorldDataModificationSemaphore = new(1, 1);

    public GPoseLobbyDistributionService(ILogger<GPoseLobbyDistributionService> logger, IRedisDatabase redisDb,
        IHubContext<MareHub, IMareHub> hubContext)
    {
        _logger = logger;
        _redisDb = redisDb;
        _hubContext = hubContext;
    }

    private bool _disposed;
    private readonly ILogger<GPoseLobbyDistributionService> _logger;
    private readonly IRedisDatabase _redisDb;
    private readonly IHubContext<MareHub, IMareHub> _hubContext;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _lobbyPoseDataModificationSemaphore.Dispose();
        _lobbyWorldDataModificationSemaphore.Dispose();

        _disposed = true;
    }

    public async Task PushWorldData(string lobby, string user, WorldData worldData)
    {
        await _lobbyWorldDataModificationSemaphore.WaitAsync().ConfigureAwait(false);
        if (!_lobbyWorldData.TryGetValue(lobby, out var worldDataDict))
        {
            _lobbyWorldData[lobby] = worldDataDict = new(StringComparer.Ordinal);
        }
        worldDataDict[user] = worldData;
        _lobbyWorldDataModificationSemaphore.Release();
    }

    public async Task PushPoseData(string lobby, string user, PoseData poseData)
    {
        await _lobbyPoseDataModificationSemaphore.WaitAsync().ConfigureAwait(false);
        if (!_lobbyPoseData.TryGetValue(lobby, out var poseDataDict))
        {
            _lobbyPoseData[lobby] = poseDataDict = new(StringComparer.Ordinal);
        }
        poseDataDict[user] = poseData;
        _lobbyPoseDataModificationSemaphore.Release();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = WorldDataDistribution(_runtimeCts.Token);
        _ = PoseDataDistribution(_runtimeCts.Token);

        return Task.CompletedTask;
    }

    private async Task WorldDataDistribution(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await DistributeWorldData(token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }
    }

    private async Task PoseDataDistribution(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await DistributePoseData(token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        }
    }

    private async Task DistributeWorldData(CancellationToken token)
    {
        await _lobbyWorldDataModificationSemaphore.WaitAsync(token).ConfigureAwait(false);
        var clone = _lobbyWorldData.ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
        _lobbyWorldData.Clear();
        _lobbyWorldDataModificationSemaphore.Release();
        foreach (var lobbyId in clone)
        {
            token.ThrowIfCancellationRequested();

            if (!lobbyId.Value.Values.Any())
                continue;

            var gposeLobbyUsers = await _redisDb.GetAsync<List<string>>($"GposeLobby:{lobbyId.Key}").ConfigureAwait(false);
            if (gposeLobbyUsers == null)
                continue;

            foreach (var data in lobbyId.Value)
            {
                await _hubContext.Clients.Users(gposeLobbyUsers.Where(k => !string.Equals(k, data.Key, StringComparison.Ordinal)))
                    .Client_GposeLobbyPushWorldData(new(data.Key), data.Value).ConfigureAwait(false);
            }
        }
    }

    private async Task DistributePoseData(CancellationToken token)
    {
        await _lobbyPoseDataModificationSemaphore.WaitAsync(token).ConfigureAwait(false);
        var clone = _lobbyPoseData.ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
        _lobbyPoseData.Clear();
        _lobbyPoseDataModificationSemaphore.Release();

        foreach (var lobbyId in clone)
        {
            token.ThrowIfCancellationRequested();

            if (!lobbyId.Value.Values.Any())
                continue;

            var gposeLobbyUsers = await _redisDb.GetAsync<List<string>>($"GposeLobby:{lobbyId.Key}").ConfigureAwait(false);
            if (gposeLobbyUsers == null)
                continue;

            foreach (var data in lobbyId.Value)
            {
                await _hubContext.Clients.Users(gposeLobbyUsers.Where(k => !string.Equals(k, data.Key, StringComparison.Ordinal)))
                    .Client_GposeLobbyPushPoseData(new(data.Key), data.Value).ConfigureAwait(false);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _runtimeCts.Cancel();
        return Task.CompletedTask;
    }
}
