using MareSynchronos.API.Data;
using MareSynchronos.API.Dto.CharaData;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    private async Task<string?> GetUserGposeLobby()
    {
        return await _redis.GetAsync<string>(GposeLobbyUser).ConfigureAwait(false);
    }

    private async Task<List<string>> GetUsersInLobby(string lobbyId, bool includeSelf = false)
    {
        var users = await _redis.GetAsync<List<string>>($"GposeLobby:{lobbyId}").ConfigureAwait(false);
        return users?.Where(u => includeSelf || !string.Equals(u, UserUID, StringComparison.Ordinal)).ToList() ?? [];
    }

    private async Task AddUserToLobby(string lobbyId, List<string> priorUsers)
    {
        _mareMetrics.IncGauge(MetricsAPI.GaugeGposeLobbyUsers);
        if (priorUsers.Count == 0)
            _mareMetrics.IncGauge(MetricsAPI.GaugeGposeLobbies);

        await _redis.AddAsync(GposeLobbyUser, lobbyId).ConfigureAwait(false);
        await _redis.AddAsync($"GposeLobby:{lobbyId}", priorUsers.Concat([UserUID])).ConfigureAwait(false);
    }

    private async Task RemoveUserFromLobby(string lobbyId, List<string> priorUsers)
    {
        await _redis.RemoveAsync(GposeLobbyUser).ConfigureAwait(false);

        _mareMetrics.DecGauge(MetricsAPI.GaugeGposeLobbyUsers);

        if (priorUsers.Count == 1)
        {
            await _redis.RemoveAsync($"GposeLobby:{lobbyId}").ConfigureAwait(false);
            _mareMetrics.DecGauge(MetricsAPI.GaugeGposeLobbies);
        }
        else
        {
            priorUsers.Remove(UserUID);
            await _redis.AddAsync($"GposeLobby:{lobbyId}", priorUsers).ConfigureAwait(false);
            await Clients.Users(priorUsers).Client_GposeLobbyLeave(new(UserUID)).ConfigureAwait(false);
        }
    }

    private string GposeLobbyUser => $"GposeLobbyUser:{UserUID}";


    [Authorize(Policy = "Identified")]
    public async Task<string> GposeLobbyCreate()
    {
        _logger.LogCallInfo();
        var alreadyInLobby = await GetUserGposeLobby().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(alreadyInLobby))
        {
            throw new HubException("Already in GPose Lobby, cannot join another");
        }

        string lobbyId = string.Empty;
        while (string.IsNullOrEmpty(lobbyId))
        {
            lobbyId = StringUtils.GenerateRandomString(30, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789");
            var result = await _redis.GetAsync<List<string>>($"GposeLobby:{lobbyId}").ConfigureAwait(false);
            if (result != null)
                lobbyId = string.Empty;
        }

        await AddUserToLobby(lobbyId, []).ConfigureAwait(false);

        return lobbyId;
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<UserData>> GposeLobbyJoin(string lobbyId)
    {
        _logger.LogCallInfo();
        var existingLobbyId = await GetUserGposeLobby().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(existingLobbyId))
            await GposeLobbyLeave().ConfigureAwait(false);

        var lobbyUsers = await GetUsersInLobby(lobbyId).ConfigureAwait(false);
        if (!lobbyUsers.Any())
            return [];

        await AddUserToLobby(lobbyId, lobbyUsers).ConfigureAwait(false);

        var user = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        await Clients.Users(lobbyUsers.Where(u => !string.Equals(u, UserUID, StringComparison.Ordinal)))
            .Client_GposeLobbyJoin(user.ToUserData()).ConfigureAwait(false);

        var users = await DbContext.Users.Where(u => lobbyUsers.Contains(u.UID))
            .Select(u => u.ToUserData())
            .ToListAsync()
            .ConfigureAwait(false);

        return users;
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GposeLobbyLeave()
    {
        var lobbyId = await GetUserGposeLobby().ConfigureAwait(false);
        if (string.IsNullOrEmpty(lobbyId))
            return true;

        _logger.LogCallInfo();

        var lobbyUsers = await GetUsersInLobby(lobbyId, true).ConfigureAwait(false);
        await RemoveUserFromLobby(lobbyId, lobbyUsers).ConfigureAwait(false);

        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task GposeLobbyPushCharacterData(CharaDataDownloadDto charaDataDownloadDto)
    {
        _logger.LogCallInfo();
        var lobbyId = await GetUserGposeLobby().ConfigureAwait(false);
        if (string.IsNullOrEmpty(lobbyId))
            return;

        var lobbyUsers = await GetUsersInLobby(lobbyId).ConfigureAwait(false);
        await Clients.Users(lobbyUsers).Client_GposeLobbyPushCharacterData(charaDataDownloadDto).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task GposeLobbyPushPoseData(PoseData poseData)
    {
        _logger.LogCallInfo();
        var lobbyId = await GetUserGposeLobby().ConfigureAwait(false);
        if (string.IsNullOrEmpty(lobbyId))
            return;

        await _gPoseLobbyDistributionService.PushPoseData(lobbyId, UserUID, poseData).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task GposeLobbyPushWorldData(WorldData worldData)
    {
        _logger.LogCallInfo();
        var lobbyId = await GetUserGposeLobby().ConfigureAwait(false);
        if (string.IsNullOrEmpty(lobbyId))
            return;

        await _gPoseLobbyDistributionService.PushWorldData(lobbyId, UserUID, worldData).ConfigureAwait(false);
    }
}