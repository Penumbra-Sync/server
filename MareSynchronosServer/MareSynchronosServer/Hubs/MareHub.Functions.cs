using MareSynchronosShared.Models;
using Microsoft.EntityFrameworkCore;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Utils;
using Microsoft.IdentityModel.Tokens;
using MareSynchronos.API.Data;
using MareSynchronos.API.Dto.Group;
using MareSynchronosShared.Metrics;
using Microsoft.AspNetCore.SignalR;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    public string UserCharaIdent => Context.User?.Claims?.SingleOrDefault(c => string.Equals(c.Type, MareClaimTypes.CharaIdent, StringComparison.Ordinal))?.Value ?? throw new Exception("No Chara Ident in Claims");

    public string UserUID => Context.User?.Claims?.SingleOrDefault(c => string.Equals(c.Type, MareClaimTypes.Uid, StringComparison.Ordinal))?.Value ?? throw new Exception("No UID in Claims");

    // TODO: add the whole permissions stuff
    private async Task DeleteUser(User user)
    {
        var ownPairData = await _dbContext.ClientPairs.Where(u => u.User.UID == user.UID).ToListAsync().ConfigureAwait(false);
        var auth = await _dbContext.Auth.SingleAsync(u => u.UserUID == user.UID).ConfigureAwait(false);
        var lodestone = await _dbContext.LodeStoneAuth.SingleOrDefaultAsync(a => a.User.UID == user.UID).ConfigureAwait(false);
        var groupPairs = await _dbContext.GroupPairs.Where(g => g.GroupUserUID == user.UID).ToListAsync().ConfigureAwait(false);
        var userProfileData = await _dbContext.UserProfileData.SingleOrDefaultAsync(u => u.UserUID == user.UID).ConfigureAwait(false);
        var defaultpermissions = await _dbContext.UserDefaultPreferredPermissions.SingleOrDefaultAsync(u => u.UserUID == user.UID).ConfigureAwait(false);
        var groupPermissions = await _dbContext.GroupPairPreferredPermissions.Where(u => u.UserUID == user.UID).ToListAsync().ConfigureAwait(false);
        var individualPermissions = await _dbContext.Permissions.Where(u => u.UserUID == user.UID || u.OtherUserUID == user.UID).ToListAsync().ConfigureAwait(false);

        if (lodestone != null)
        {
            _dbContext.Remove(lodestone);
        }

        if (userProfileData != null)
        {
            _dbContext.Remove(userProfileData);
        }

        while (_dbContext.Files.Any(f => f.Uploader == user))
        {
            await Task.Delay(1000).ConfigureAwait(false);
        }

        _dbContext.ClientPairs.RemoveRange(ownPairData);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        var otherPairData = await _dbContext.ClientPairs.Include(u => u.User)
            .Where(u => u.OtherUser.UID == user.UID).AsNoTracking().ToListAsync().ConfigureAwait(false);
        foreach (var pair in otherPairData)
        {
            await Clients.User(pair.UserUID).Client_UserRemoveClientPair(new(user.ToUserData())).ConfigureAwait(false);
        }

        foreach (var pair in groupPairs)
        {
            await UserLeaveGroup(new GroupDto(new GroupData(pair.GroupGID)), user.UID).ConfigureAwait(false);
        }

        _dbContext.GroupPairPreferredPermissions.RemoveRange(groupPermissions);
        _dbContext.Permissions.RemoveRange(individualPermissions);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _mareMetrics.IncCounter(MetricsAPI.CounterUsersRegisteredDeleted, 1);

        _dbContext.ClientPairs.RemoveRange(otherPairData);
        _dbContext.Users.Remove(user);
        _dbContext.Auth.Remove(auth);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _cacheService.ClearCache(user.UID);
        _cacheService.MarkAsStale(null, user.UID);
    }

    private async Task<List<string>> GetAllPairedUnpausedUsers(string? uid = null)
    {
        uid ??= UserUID;

        return (await _cacheService.GetAllPairs(UserUID).ConfigureAwait(false))
            .Where(u => !u.Value.OwnPermissions.IsPaused && u.Value.OtherPermissions != null && !u.Value.OtherPermissions.IsPaused)
            .Select(u => u.Key).ToList();
    }

    private async Task<Dictionary<string, string>> GetOnlineUsers(List<string> uids)
    {
        var result = await _redis.GetAllAsync<string>(uids.Select(u => "UID:" + u).ToHashSet(StringComparer.Ordinal)).ConfigureAwait(false);
        return uids.Where(u => result.TryGetValue("UID:" + u, out var ident) && !string.IsNullOrEmpty(ident)).ToDictionary(u => u, u => result["UID:" + u], StringComparer.Ordinal);
    }

    private async Task<string> GetUserIdent(string uid)
    {
        if (uid.IsNullOrEmpty()) return string.Empty;
        return await _redis.GetAsync<string>("UID:" + uid).ConfigureAwait(false);
    }

    private async Task RemoveUserFromRedis()
    {
        await _redis.RemoveAsync("UID:" + UserUID, StackExchange.Redis.CommandFlags.FireAndForget).ConfigureAwait(false);
    }

    private async Task SendGroupDeletedToAll(List<GroupPair> groupUsers)
    {
        foreach (var pair in groupUsers)
        {
            var pairIdent = await GetUserIdent(pair.GroupUserUID).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pairIdent)) continue;

            foreach (var groupUserPair in groupUsers.Where(g => !string.Equals(g.GroupUserUID, pair.GroupUserUID, StringComparison.Ordinal)))
            {
                await UserGroupLeave(groupUserPair, pairIdent, pair.GroupUserUID).ConfigureAwait(false);
            }
        }
    }

    private async Task<List<string>> SendOfflineToAllPairedUsers()
    {
        var usersToSendDataTo = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var self = await _dbContext.Users.AsNoTracking().SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        await Clients.Users(usersToSendDataTo).Client_UserSendOffline(new(self.ToUserData())).ConfigureAwait(false);

        return usersToSendDataTo;
    }

    private async Task<List<string>> SendOnlineToAllPairedUsers()
    {
        var usersToSendDataTo = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var self = await _dbContext.Users.AsNoTracking().SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        await Clients.Users(usersToSendDataTo).Client_UserSendOnline(new(self.ToUserData(), UserCharaIdent)).ConfigureAwait(false);

        return usersToSendDataTo;
    }

    private async Task<(bool IsValid, Group ReferredGroup)> TryValidateGroupModeratorOrOwner(string gid)
    {
        var isOwnerResult = await TryValidateOwner(gid).ConfigureAwait(false);
        if (isOwnerResult.isValid) return (true, isOwnerResult.ReferredGroup);

        if (isOwnerResult.ReferredGroup == null) return (false, null);

        var groupPairSelf = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == UserUID).ConfigureAwait(false);
        if (groupPairSelf == null || !groupPairSelf.IsModerator) return (false, null);

        return (true, isOwnerResult.ReferredGroup);
    }

    private async Task<(bool isValid, Group ReferredGroup)> TryValidateOwner(string gid)
    {
        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
        if (group == null) return (false, null);

        return (string.Equals(group.OwnerUID, UserUID, StringComparison.Ordinal), group);
    }

    private async Task<(bool IsValid, GroupPair ReferredPair)> TryValidateUserInGroup(string gid, string? uid = null)
    {
        uid ??= UserUID;

        var groupPair = await _dbContext.GroupPairs.Include(c => c.GroupUser)
            .SingleOrDefaultAsync(g => g.GroupGID == gid && (g.GroupUserUID == uid || g.GroupUser.Alias == uid)).ConfigureAwait(false);
        if (groupPair == null) return (false, null);

        return (true, groupPair);
    }

    private async Task UpdateUserOnRedis()
    {
        await _redis.AddAsync("UID:" + UserUID, UserCharaIdent, TimeSpan.FromSeconds(60), StackExchange.Redis.When.Always, StackExchange.Redis.CommandFlags.FireAndForget).ConfigureAwait(false);
    }

    private async Task UserGroupLeave(GroupPair groupUserPair, string userIdent, string? uid = null)
    {
        uid ??= UserUID;
        var allUserPairs = await _cacheService.GetAllPairs(uid).ConfigureAwait(false);
        if (!allUserPairs.TryGetValue(groupUserPair.GroupUserUID, out var info) || !info.IsSynced)
        {
            var groupUserIdent = await GetUserIdent(groupUserPair.GroupUserUID).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(groupUserIdent))
            {
                await Clients.User(uid).Client_UserSendOffline(new(new(groupUserPair.GroupUserUID))).ConfigureAwait(false);
                await Clients.User(groupUserPair.GroupUserUID).Client_UserSendOffline(new(new(uid))).ConfigureAwait(false);
            }
        }
    }

    private async Task UserLeaveGroup(GroupDto dto, string userUid)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (exists, groupPair) = await TryValidateUserInGroup(dto.Group.GID, userUid).ConfigureAwait(false);
        if (!exists) return;

        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == dto.Group.GID).ConfigureAwait(false);

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == group.GID).ToListAsync().ConfigureAwait(false);
        var groupPairsWithoutSelf = groupPairs.Where(p => !string.Equals(p.GroupUserUID, userUid, StringComparison.Ordinal)).ToList();

        _dbContext.GroupPairs.Remove(groupPair);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        await Clients.User(userUid).Client_GroupDelete(new GroupDto(group.ToGroupData())).ConfigureAwait(false);

        bool ownerHasLeft = string.Equals(group.OwnerUID, userUid, StringComparison.Ordinal);
        if (ownerHasLeft)
        {
            if (!groupPairsWithoutSelf.Any())
            {
                _logger.LogCallInfo(MareHubLogger.Args(dto, "Deleted"));

                _dbContext.Groups.Remove(group);
            }
            else
            {
                var groupHasMigrated = await SharedDbFunctions.MigrateOrDeleteGroup(_dbContext, group, groupPairsWithoutSelf, _maxExistingGroupsByUser).ConfigureAwait(false);

                if (groupHasMigrated.Item1)
                {
                    _logger.LogCallInfo(MareHubLogger.Args(dto, "Migrated", groupHasMigrated.Item2));

                    var user = await _dbContext.Users.SingleAsync(u => u.UID == groupHasMigrated.Item2).ConfigureAwait(false);

                    await Clients.Users(groupPairsWithoutSelf.Select(p => p.GroupUserUID)).Client_GroupSendInfo(new GroupInfoDto(group.ToGroupData(),
                        user.ToUserData(), group.ToEnum())).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogCallInfo(MareHubLogger.Args(dto, "Deleted"));

                    await Clients.Users(groupPairsWithoutSelf.Select(p => p.GroupUserUID)).Client_GroupDelete(dto).ConfigureAwait(false);

                    await SendGroupDeletedToAll(groupPairs).ConfigureAwait(false);

                    return;
                }
            }
        }

        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _cacheService.MarkAsStale(userUid, null);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        await Clients.Users(groupPairsWithoutSelf.Select(p => p.GroupUserUID)).Client_GroupPairLeft(new GroupPairDto(dto.Group, groupPair.GroupUser.ToUserData())).ConfigureAwait(false);

        var ident = await GetUserIdent(userUid).ConfigureAwait(false);

        foreach (var groupUserPair in groupPairsWithoutSelf)
        {
            await UserGroupLeave(groupUserPair, ident, userUid).ConfigureAwait(false);
        }
    }

    public record UserQueryPermissionEntry(UserQueryEntry OtherUser, UserPermissionSet OwnPermissions, UserPermissionSet? OtherPermissions);
    public record UserQueryEntry(string UID, bool IsPaired, string GID, string Alias);
}