using MareSynchronosShared.Models;
using Microsoft.EntityFrameworkCore;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Utils;
using MareSynchronos.API.Data;
using MareSynchronos.API.Dto.Group;
using MareSynchronosShared.Metrics;
using Microsoft.AspNetCore.SignalR;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    public string UserCharaIdent => Context.User?.Claims?.SingleOrDefault(c => string.Equals(c.Type, MareClaimTypes.CharaIdent, StringComparison.Ordinal))?.Value ?? throw new Exception("No Chara Ident in Claims");

    public string UserUID => Context.User?.Claims?.SingleOrDefault(c => string.Equals(c.Type, MareClaimTypes.Uid, StringComparison.Ordinal))?.Value ?? throw new Exception("No UID in Claims");

    public string Continent => Context.User?.Claims?.SingleOrDefault(c => string.Equals(c.Type, MareClaimTypes.Continent, StringComparison.Ordinal))?.Value ?? "UNK";

    private async Task DeleteUser(User user)
    {
        var ownPairData = await DbContext.ClientPairs.Where(u => u.User.UID == user.UID).ToListAsync().ConfigureAwait(false);
        var auth = await DbContext.Auth.SingleAsync(u => u.UserUID == user.UID).ConfigureAwait(false);
        var lodestone = await DbContext.LodeStoneAuth.SingleOrDefaultAsync(a => a.User.UID == user.UID).ConfigureAwait(false);
        var groupPairs = await DbContext.GroupPairs.Where(g => g.GroupUserUID == user.UID).ToListAsync().ConfigureAwait(false);
        var userProfileData = await DbContext.UserProfileData.SingleOrDefaultAsync(u => u.UserUID == user.UID).ConfigureAwait(false);
        var defaultpermissions = await DbContext.UserDefaultPreferredPermissions.SingleOrDefaultAsync(u => u.UserUID == user.UID).ConfigureAwait(false);
        var groupPermissions = await DbContext.GroupPairPreferredPermissions.Where(u => u.UserUID == user.UID).ToListAsync().ConfigureAwait(false);
        var individualPermissions = await DbContext.Permissions.Where(u => u.UserUID == user.UID || u.OtherUserUID == user.UID).ToListAsync().ConfigureAwait(false);
        var bannedEntries = await DbContext.GroupBans.Where(u => u.BannedUserUID == user.UID).ToListAsync().ConfigureAwait(false);

        if (lodestone != null)
        {
            DbContext.Remove(lodestone);
        }

        if (userProfileData != null)
        {
            DbContext.Remove(userProfileData);
        }

        while (DbContext.Files.Any(f => f.Uploader == user))
        {
            await Task.Delay(1000).ConfigureAwait(false);
        }

        DbContext.ClientPairs.RemoveRange(ownPairData);
        var otherPairData = await DbContext.ClientPairs.Include(u => u.User)
            .Where(u => u.OtherUser.UID == user.UID).AsNoTracking().ToListAsync().ConfigureAwait(false);
        foreach (var pair in otherPairData)
        {
            await Clients.User(pair.UserUID).Client_UserRemoveClientPair(new(user.ToUserData())).ConfigureAwait(false);
        }

        foreach (var pair in groupPairs)
        {
            await UserLeaveGroup(new GroupDto(new GroupData(pair.GroupGID)), user.UID).ConfigureAwait(false);
        }

        if (defaultpermissions != null)
        {
            DbContext.UserDefaultPreferredPermissions.Remove(defaultpermissions);
        }
        DbContext.GroupPairPreferredPermissions.RemoveRange(groupPermissions);
        DbContext.Permissions.RemoveRange(individualPermissions);
        DbContext.GroupBans.RemoveRange(bannedEntries);

        _mareMetrics.IncCounter(MetricsAPI.CounterUsersRegisteredDeleted, 1);

        DbContext.ClientPairs.RemoveRange(otherPairData);
        DbContext.Users.Remove(user);
        DbContext.Auth.Remove(auth);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<List<string>> GetAllPairedUnpausedUsers(string? uid = null)
    {
        uid ??= UserUID;

        return (await GetSyncedUnpausedOnlinePairs(UserUID).ConfigureAwait(false));
    }

    private async Task<Dictionary<string, string>> GetOnlineUsers(List<string> uids)
    {
        var result = await _redis.GetAllAsync<string>(uids.Select(u => "UID:" + u).ToHashSet(StringComparer.Ordinal)).ConfigureAwait(false);
        return uids.Where(u => result.TryGetValue("UID:" + u, out var ident) && !string.IsNullOrEmpty(ident)).ToDictionary(u => u, u => result["UID:" + u], StringComparer.Ordinal);
    }

    private async Task<string> GetUserIdent(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return string.Empty;
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

            var pairInfo = await GetAllPairInfo(pair.GroupUserUID).ConfigureAwait(false);

            foreach (var groupUserPair in groupUsers.Where(g => !string.Equals(g.GroupUserUID, pair.GroupUserUID, StringComparison.Ordinal)))
            {
                await UserGroupLeave(groupUserPair, pairIdent, pairInfo, pair.GroupUserUID).ConfigureAwait(false);
            }
        }
    }

    private async Task<List<string>> SendOfflineToAllPairedUsers()
    {
        var usersToSendDataTo = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var self = await DbContext.Users.AsNoTracking().SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        await Clients.Users(usersToSendDataTo).Client_UserSendOffline(new(self.ToUserData())).ConfigureAwait(false);

        return usersToSendDataTo;
    }

    private async Task<List<string>> SendOnlineToAllPairedUsers()
    {
        var usersToSendDataTo = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var self = await DbContext.Users.AsNoTracking().SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        await Clients.Users(usersToSendDataTo).Client_UserSendOnline(new(self.ToUserData(), UserCharaIdent)).ConfigureAwait(false);

        return usersToSendDataTo;
    }

    private async Task<(bool IsValid, Group ReferredGroup)> TryValidateGroupModeratorOrOwner(string gid)
    {
        var isOwnerResult = await TryValidateOwner(gid).ConfigureAwait(false);
        if (isOwnerResult.isValid) return (true, isOwnerResult.ReferredGroup);

        if (isOwnerResult.ReferredGroup == null) return (false, null);

        var groupPairSelf = await DbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == UserUID).ConfigureAwait(false);
        if (groupPairSelf == null || !groupPairSelf.IsModerator) return (false, null);

        return (true, isOwnerResult.ReferredGroup);
    }

    private async Task<(bool isValid, Group ReferredGroup)> TryValidateOwner(string gid)
    {
        var group = await DbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
        if (group == null) return (false, null);

        return (string.Equals(group.OwnerUID, UserUID, StringComparison.Ordinal), group);
    }

    private async Task<(bool IsValid, GroupPair ReferredPair)> TryValidateUserInGroup(string gid, string? uid = null)
    {
        uid ??= UserUID;

        var groupPair = await DbContext.GroupPairs.Include(c => c.GroupUser)
            .SingleOrDefaultAsync(g => g.GroupGID == gid && (g.GroupUserUID == uid || g.GroupUser.Alias == uid)).ConfigureAwait(false);
        if (groupPair == null) return (false, null);

        return (true, groupPair);
    }

    private async Task UpdateUserOnRedis()
    {
        await _redis.AddAsync("UID:" + UserUID, UserCharaIdent, TimeSpan.FromSeconds(60), StackExchange.Redis.When.Always, StackExchange.Redis.CommandFlags.FireAndForget).ConfigureAwait(false);
    }

    private async Task UserGroupLeave(GroupPair groupUserPair, string userIdent, Dictionary<string, UserInfo> allUserPairs, string? uid = null)
    {
        uid ??= UserUID;
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

        var group = await DbContext.Groups.SingleOrDefaultAsync(g => g.GID == dto.Group.GID).ConfigureAwait(false);

        var groupPairs = await DbContext.GroupPairs.Where(p => p.GroupGID == group.GID).ToListAsync().ConfigureAwait(false);
        var groupPairsWithoutSelf = groupPairs.Where(p => !string.Equals(p.GroupUserUID, userUid, StringComparison.Ordinal)).ToList();

        DbContext.GroupPairs.Remove(groupPair);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        await Clients.User(userUid).Client_GroupDelete(new GroupDto(group.ToGroupData())).ConfigureAwait(false);

        bool ownerHasLeft = string.Equals(group.OwnerUID, userUid, StringComparison.Ordinal);
        if (ownerHasLeft)
        {
            if (!groupPairsWithoutSelf.Any())
            {
                _logger.LogCallInfo(MareHubLogger.Args(dto, "Deleted"));

                DbContext.Groups.Remove(group);
            }
            else
            {
                var groupHasMigrated = await SharedDbFunctions.MigrateOrDeleteGroup(DbContext, group, groupPairsWithoutSelf, _maxExistingGroupsByUser).ConfigureAwait(false);

                if (groupHasMigrated.Item1)
                {
                    _logger.LogCallInfo(MareHubLogger.Args(dto, "Migrated", groupHasMigrated.Item2));

                    var user = await DbContext.Users.SingleAsync(u => u.UID == groupHasMigrated.Item2).ConfigureAwait(false);

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

        var sharedData = await DbContext.CharaDataAllowances.Where(u => u.AllowedGroup != null && u.AllowedGroupGID == dto.GID && u.ParentUploaderUID == userUid).ToListAsync().ConfigureAwait(false);
        DbContext.CharaDataAllowances.RemoveRange(sharedData);

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        await Clients.Users(groupPairsWithoutSelf.Select(p => p.GroupUserUID)).Client_GroupPairLeft(new GroupPairDto(dto.Group, groupPair.GroupUser.ToUserData())).ConfigureAwait(false);

        var ident = await GetUserIdent(userUid).ConfigureAwait(false);

        var pairs = await GetAllPairInfo(userUid).ConfigureAwait(false);

        foreach (var groupUserPair in groupPairsWithoutSelf)
        {
            await UserGroupLeave(groupUserPair, ident, pairs, userUid).ConfigureAwait(false);
        }
    }

    private async Task<UserInfo?> GetPairInfo(string uid, string otheruid)
    {
        var clientPairs = from cp in DbContext.ClientPairs.AsNoTracking().Where(u => u.UserUID == uid && u.OtherUserUID == otheruid)
                          join cp2 in DbContext.ClientPairs.AsNoTracking().Where(u => u.OtherUserUID == uid && u.UserUID == otheruid)
                          on new
                          {
                              UserUID = cp.UserUID,
                              OtherUserUID = cp.OtherUserUID
                          }
                          equals new
                          {
                              UserUID = cp2.OtherUserUID,
                              OtherUserUID = cp2.UserUID
                          } into joined
                          from c in joined.DefaultIfEmpty()
                          where cp.UserUID == uid
                          select new
                          {
                              UserUID = cp.UserUID,
                              OtherUserUID = cp.OtherUserUID,
                              Gid = string.Empty,
                              Synced = c != null
                          };


        var groupPairs = from gp in DbContext.GroupPairs.AsNoTracking().Where(u => u.GroupUserUID == uid)
                         join gp2 in DbContext.GroupPairs.AsNoTracking().Where(u => u.GroupUserUID == otheruid)
                         on new
                         {
                             GID = gp.GroupGID
                         }
                         equals new
                         {
                             GID = gp2.GroupGID
                         }
                         where gp.GroupUserUID == uid
                         select new
                         {
                             UserUID = gp.GroupUserUID,
                             OtherUserUID = gp2.GroupUserUID,
                             Gid = Convert.ToString(gp2.GroupGID),
                             Synced = true
                         };

        var allPairs = clientPairs.Concat(groupPairs);

        var result = from user in allPairs
                     join u in DbContext.Users.AsNoTracking() on user.OtherUserUID equals u.UID
                     join o in DbContext.Permissions.AsNoTracking().Where(u => u.UserUID == uid)
                        on new { UserUID = user.UserUID, OtherUserUID = user.OtherUserUID }
                        equals new { UserUID = o.UserUID, OtherUserUID = o.OtherUserUID }
                        into ownperms
                     from ownperm in ownperms.DefaultIfEmpty()
                     join p in DbContext.Permissions.AsNoTracking().Where(u => u.OtherUserUID == uid)
                        on new { UserUID = user.OtherUserUID, OtherUserUID = user.UserUID }
                        equals new { UserUID = p.UserUID, OtherUserUID = p.OtherUserUID }
                        into otherperms
                     from otherperm in otherperms.DefaultIfEmpty()
                     where user.UserUID == uid
                        && u.UID == user.OtherUserUID
                        && ownperm.UserUID == user.UserUID && ownperm.OtherUserUID == user.OtherUserUID
                        && (otherperm == null || (otherperm.OtherUserUID == user.UserUID && otherperm.UserUID == user.OtherUserUID))
                     select new
                     {
                         UserUID = user.UserUID,
                         OtherUserUID = user.OtherUserUID,
                         OtherUserAlias = u.Alias,
                         GID = user.Gid,
                         Synced = user.Synced,
                         OwnPermissions = ownperm,
                         OtherPermissions = otherperm
                     };

        var resultList = await result.AsNoTracking().ToListAsync().ConfigureAwait(false);

        if (!resultList.Any()) return null;

        var groups = resultList.Select(g => g.GID).ToList();
        return new UserInfo(resultList[0].OtherUserAlias,
            resultList.SingleOrDefault(p => string.IsNullOrEmpty(p.GID))?.Synced ?? false,
            resultList.Max(p => p.Synced),
            resultList.Select(p => string.IsNullOrEmpty(p.GID) ? Constants.IndividualKeyword : p.GID).ToList(),
            resultList[0].OwnPermissions,
            resultList[0].OtherPermissions);
    }

    private async Task<Dictionary<string, UserInfo>> GetAllPairInfo(string uid)
    {
        var clientPairs = from cp in DbContext.ClientPairs.AsNoTracking().Where(u => u.UserUID == uid)
                          join cp2 in DbContext.ClientPairs.AsNoTracking().Where(u => u.OtherUserUID == uid)
                          on new
                          {
                              UserUID = cp.UserUID,
                              OtherUserUID = cp.OtherUserUID
                          }
                          equals new
                          {
                              UserUID = cp2.OtherUserUID,
                              OtherUserUID = cp2.UserUID
                          } into joined
                          from c in joined.DefaultIfEmpty()
                          where cp.UserUID == uid
                          select new
                          {
                              UserUID = cp.UserUID,
                              OtherUserUID = cp.OtherUserUID,
                              Gid = string.Empty,
                              Synced = c != null
                          };


        var groupPairs = from gp in DbContext.GroupPairs.AsNoTracking().Where(u => u.GroupUserUID == uid)
                         join gp2 in DbContext.GroupPairs.AsNoTracking().Where(u => u.GroupUserUID != uid)
                         on new
                         {
                             GID = gp.GroupGID
                         }
                         equals new
                         {
                             GID = gp2.GroupGID
                         }
                         select new
                         {
                             UserUID = gp.GroupUserUID,
                             OtherUserUID = gp2.GroupUserUID,
                             Gid = Convert.ToString(gp2.GroupGID),
                             Synced = true
                         };

        var allPairs = clientPairs.Concat(groupPairs);

        var result = from user in allPairs
                     join u in DbContext.Users.AsNoTracking() on user.OtherUserUID equals u.UID
                     join o in DbContext.Permissions.AsNoTracking().Where(u => u.UserUID == uid)
                        on new { UserUID = user.UserUID, OtherUserUID = user.OtherUserUID }
                        equals new { UserUID = o.UserUID, OtherUserUID = o.OtherUserUID }
                        into ownperms
                     from ownperm in ownperms.DefaultIfEmpty()
                     join p in DbContext.Permissions.AsNoTracking().Where(u => u.OtherUserUID == uid)
                        on new { UserUID = user.OtherUserUID, OtherUserUID = user.UserUID }
                        equals new { UserUID = p.UserUID, OtherUserUID = p.OtherUserUID }
                        into otherperms
                     from otherperm in otherperms.DefaultIfEmpty()
                     where user.UserUID == uid
                        && u.UID == user.OtherUserUID
                        && ownperm.UserUID == user.UserUID && ownperm.OtherUserUID == user.OtherUserUID
                        && (otherperm == null || (otherperm.OtherUserUID == user.UserUID && otherperm.UserUID == user.OtherUserUID))
                     select new
                     {
                         UserUID = user.UserUID,
                         OtherUserUID = user.OtherUserUID,
                         OtherUserAlias = u.Alias,
                         GID = user.Gid,
                         Synced = user.Synced,
                         OwnPermissions = ownperm,
                         OtherPermissions = otherperm
                     };

        var resultList = await result.AsNoTracking().ToListAsync().ConfigureAwait(false);
        return resultList.GroupBy(g => g.OtherUserUID, StringComparer.Ordinal).ToDictionary(g => g.Key, g =>
        {
            return new UserInfo(g.First().OtherUserAlias,
                g.SingleOrDefault(p => string.IsNullOrEmpty(p.GID))?.Synced ?? false,
                g.Max(p => p.Synced),
                g.Select(p => string.IsNullOrEmpty(p.GID) ? Constants.IndividualKeyword : p.GID).ToList(),
                g.First().OwnPermissions,
                g.First().OtherPermissions);
        }, StringComparer.Ordinal);
    }

    private async Task<List<string>> GetSyncedUnpausedOnlinePairs(string uid)
    {
        var clientPairs = from cp in DbContext.ClientPairs.AsNoTracking().Where(u => u.UserUID == uid)
                          join cp2 in DbContext.ClientPairs.AsNoTracking().Where(u => u.OtherUserUID == uid)
                          on new
                          {
                              UserUID = cp.UserUID,
                              OtherUserUID = cp.OtherUserUID
                          }
                          equals new
                          {
                              UserUID = cp2.OtherUserUID,
                              OtherUserUID = cp2.UserUID
                          } into joined
                          from c in joined.DefaultIfEmpty()
                          where cp.UserUID == uid && c.UserUID != null
                          select new
                          {
                              UserUID = cp.UserUID,
                              OtherUserUID = cp.OtherUserUID,
                          };


        var groupPairs = from gp in DbContext.GroupPairs.AsNoTracking().Where(u => u.GroupUserUID == uid)
                         join gp2 in DbContext.GroupPairs.AsNoTracking().Where(u => u.GroupUserUID != uid)
                         on new
                         {
                             GID = gp.GroupGID
                         }
                         equals new
                         {
                             GID = gp2.GroupGID
                         }
                         select new
                         {
                             UserUID = gp.GroupUserUID,
                             OtherUserUID = gp2.GroupUserUID,
                         };

        var allPairs = clientPairs.Concat(groupPairs);

        var result = from user in allPairs
                     join o in DbContext.Permissions.AsNoTracking().Where(u => u.UserUID == uid)
                        on new { UserUID = user.UserUID, OtherUserUID = user.OtherUserUID }
                        equals new { UserUID = o.UserUID, OtherUserUID = o.OtherUserUID }
                        into ownperms
                     from ownperm in ownperms.DefaultIfEmpty()
                     join p in DbContext.Permissions.AsNoTracking().Where(u => u.OtherUserUID == uid)
                        on new { UserUID = user.OtherUserUID, OtherUserUID = user.UserUID }
                        equals new { UserUID = p.UserUID, OtherUserUID = p.OtherUserUID }
                        into otherperms
                     from otherperm in otherperms.DefaultIfEmpty()
                     where user.UserUID == uid
                        && ownperm.UserUID == user.UserUID && ownperm.OtherUserUID == user.OtherUserUID
                        && otherperm.OtherUserUID == user.UserUID && otherperm.UserUID == user.OtherUserUID
                        && !ownperm.IsPaused && (otherperm == null ? false : !otherperm.IsPaused)
                     select user.OtherUserUID;

        return await result.Distinct().AsNoTracking().ToListAsync().ConfigureAwait(false);
    }

    public record UserInfo(string Alias, bool IndividuallyPaired, bool IsSynced, List<string> GIDs, UserPermissionSet? OwnPermissions, UserPermissionSet? OtherPermissions);
}