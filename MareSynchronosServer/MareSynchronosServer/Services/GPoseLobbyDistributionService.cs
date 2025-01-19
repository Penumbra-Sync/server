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

        _runtimeCts.Cancel();
        _runtimeCts.Dispose();
        _lobbyPoseDataModificationSemaphore.Dispose();
        _lobbyWorldDataModificationSemaphore.Dispose();

        _disposed = true;
    }

    public async Task PushWorldData(string lobby, string user, WorldData worldData)
    {
        await _lobbyWorldDataModificationSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_lobbyWorldData.TryGetValue(lobby, out var worldDataDict))
            {
                _lobbyWorldData[lobby] = worldDataDict = new(StringComparer.Ordinal);
            }
            worldDataDict[user] = worldData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Pushing World Data for Lobby {lobby} by User {user}", lobby, user);
        }
        finally
        {
            _lobbyWorldDataModificationSemaphore.Release();
        }
    }

    public async Task PushPoseData(string lobby, string user, PoseData poseData)
    {
        await _lobbyPoseDataModificationSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_lobbyPoseData.TryGetValue(lobby, out var poseDataDict))
            {
                _lobbyPoseData[lobby] = poseDataDict = new(StringComparer.Ordinal);
            }
            poseDataDict[user] = poseData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Pushing World Data for Lobby {lobby} by User {user}", lobby, user);
        }
        finally
        {
            _lobbyPoseDataModificationSemaphore.Release();
        }
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
            try
            {
                await DistributeWorldData(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during World Data Distribution");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }
    }

    private async Task PoseDataDistribution(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await DistributePoseData(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Pose Data Distribution");
            }
            await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        }
    }

    private async Task DistributeWorldData(CancellationToken token)
    {
        await _lobbyWorldDataModificationSemaphore.WaitAsync(token).ConfigureAwait(false);
        Dictionary<string, Dictionary<string, WorldData>> clone = [];
        try
        {
            clone = _lobbyWorldData.ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
            _lobbyWorldData.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Distributing World Data Clone generation");
            _lobbyWorldData.Clear();
            return;
        }
        finally
        {
            _lobbyWorldDataModificationSemaphore.Release();
        }

        foreach (var lobbyId in clone)
        {
            token.ThrowIfCancellationRequested();

            try
            {
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during World Data Distribution for Lobby {lobby}", lobbyId.Key);
                continue;
            }
        }
    }

    private async Task DistributePoseData(CancellationToken token)
    {
        await _lobbyPoseDataModificationSemaphore.WaitAsync(token).ConfigureAwait(false);
        Dictionary<string, Dictionary<string, PoseData>> clone = [];
        try
        {
            clone = _lobbyPoseData.ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
            _lobbyPoseData.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Distributing Pose Data Clone generation");
            _lobbyPoseData.Clear();
            return;
        }
        finally
        {
            _lobbyPoseDataModificationSemaphore.Release();
        }

        foreach (var lobbyId in clone)
        {
            token.ThrowIfCancellationRequested();

            try
            {
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Pose Data Distribution for Lobby {lobby}", lobbyId.Key);
                continue;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _runtimeCts.Cancel();
        return Task.CompletedTask;
    }
}
