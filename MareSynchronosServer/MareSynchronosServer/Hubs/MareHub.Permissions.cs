using MareSynchronos.API.Data.Enum;
using MareSynchronos.API.Dto;
using MareSynchronos.API.Dto.Group;
using MareSynchronos.API.Dto.User;
using MareSynchronos.API.Data.Extensions;
using MareSynchronosServer.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    [Authorize(Policy = "Authenticated")]
    public async Task UserUpdateDefaultPermissions(DefaultPermissionsDto defaultPermissions)
    {
        _logger.LogCallInfo(MareHubLogger.Args(defaultPermissions));

        var permissions = await DbContext.UserDefaultPreferredPermissions.SingleAsync(u => u.UserUID == UserUID).ConfigureAwait(false);

        permissions.DisableGroupAnimations = defaultPermissions.DisableGroupAnimations;
        permissions.DisableGroupSounds = defaultPermissions.DisableGroupSounds;
        permissions.DisableGroupVFX = defaultPermissions.DisableGroupVFX;
        permissions.DisableIndividualAnimations = defaultPermissions.DisableIndividualAnimations;
        permissions.DisableIndividualSounds = defaultPermissions.DisableIndividualSounds;
        permissions.DisableIndividualVFX = defaultPermissions.DisableIndividualVFX;
        permissions.IndividualIsSticky = defaultPermissions.IndividualIsSticky;

        DbContext.Update(permissions);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        await Clients.Caller.Client_UserUpdateDefaultPermissions(defaultPermissions).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task SetBulkPermissions(BulkPermissionsDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(
            "Individual", string.Join(';', dto.AffectedUsers.Select(g => g.Key + ":" + g.Value)),
            "Group", string.Join(';', dto.AffectedGroups.Select(g => g.Key + ":" + g.Value))));

        // remove self
        dto.AffectedUsers.Remove(UserUID, out _);
        if (!dto.AffectedUsers.Any() && !dto.AffectedGroups.Any()) return;

        // get all current pairs in any form
        var allUsers = await GetAllPairInfo(UserUID).ConfigureAwait(false);
        var ownDefaultPerms = await DbContext.UserDefaultPreferredPermissions.SingleAsync(u => u.UserUID == UserUID).ConfigureAwait(false);

        foreach (var user in dto.AffectedUsers)
        {
            bool setSticky = false;
            var newPerm = user.Value;
            if (!allUsers.TryGetValue(user.Key, out var pairData)) continue;
            if (!pairData.OwnPermissions.Sticky && !newPerm.IsSticky())
            {
                setSticky = ownDefaultPerms.IndividualIsSticky;
            }

            var pauseChange = pairData.OwnPermissions.IsPaused != newPerm.IsPaused();
            var prevPermissions = await DbContext.Permissions.SingleAsync(u => u.UserUID == UserUID && u.OtherUserUID == user.Key).ConfigureAwait(false);

            prevPermissions.IsPaused = newPerm.IsPaused();
            prevPermissions.DisableAnimations = newPerm.IsDisableAnimations();
            prevPermissions.DisableSounds = newPerm.IsDisableSounds();
            prevPermissions.DisableVFX = newPerm.IsDisableVFX();
            prevPermissions.Sticky = newPerm.IsSticky() || setSticky;
            DbContext.Update(prevPermissions);

            // send updated data to pair
            var permCopy = newPerm;
            permCopy.SetSticky(newPerm.IsSticky() || setSticky);
            var permToOther = permCopy;
            permToOther.SetSticky(false);

            await Clients.User(UserUID).Client_UserUpdateSelfPairPermissions(new(new(user.Key), permCopy)).ConfigureAwait(false);
            if (pairData.OtherPermissions == null) continue;

            await Clients.User(user.Key).Client_UserUpdateOtherPairPermissions(new(new(UserUID), permToOther)).ConfigureAwait(false);

            // check if pause change and send online or offline respectively
            if (pauseChange && !pairData.OtherPermissions.IsPaused)
            {
                var otherCharaIdent = await GetUserIdent(user.Key).ConfigureAwait(false);

                if (UserCharaIdent == null || otherCharaIdent == null) continue;

                if (newPerm.IsPaused())
                {
                    await Clients.User(UserUID).Client_UserSendOffline(new(new(user.Key))).ConfigureAwait(false);
                    await Clients.User(user.Key).Client_UserSendOffline(new(new(UserUID))).ConfigureAwait(false);
                }
                else
                {
                    await Clients.User(UserUID).Client_UserSendOnline(new(new(user.Key), otherCharaIdent)).ConfigureAwait(false);
                    await Clients.User(user.Key).Client_UserSendOnline(new(new(UserUID), UserCharaIdent)).ConfigureAwait(false);
                }
            }
        }

        foreach (var group in dto.AffectedGroups)
        {
            var (inGroup, groupPair) = await TryValidateUserInGroup(group.Key).ConfigureAwait(false);
            if (!inGroup) continue;

            var groupPreferredPermissions = await DbContext.GroupPairPreferredPermissions
                .SingleAsync(u => u.UserUID == UserUID && u.GroupGID == group.Key).ConfigureAwait(false);

            var wasPaused = groupPreferredPermissions.IsPaused;
            groupPreferredPermissions.DisableSounds = group.Value.IsDisableSounds();
            groupPreferredPermissions.DisableAnimations = group.Value.IsDisableAnimations();
            groupPreferredPermissions.IsPaused = group.Value.IsPaused();
            groupPreferredPermissions.DisableVFX = group.Value.IsDisableVFX();

            var nonStickyPairs = allUsers.Where(u => !u.Value.OwnPermissions.Sticky).ToList();
            var affectedGroupPairs = nonStickyPairs.Where(u => u.Value.GIDs.Contains(group.Key, StringComparer.Ordinal)).ToList();
            var groupUserUids = affectedGroupPairs.Select(g => g.Key).ToList();
            var affectedPerms = await DbContext.Permissions.Where(u => u.UserUID == UserUID
                && groupUserUids.Any(c => c == u.OtherUserUID))
                .ToListAsync().ConfigureAwait(false);

            foreach (var perm in affectedPerms)
            {
                perm.DisableSounds = groupPreferredPermissions.DisableSounds;
                perm.DisableAnimations = groupPreferredPermissions.DisableAnimations;
                perm.IsPaused = groupPreferredPermissions.IsPaused;
                perm.DisableVFX = groupPreferredPermissions.DisableVFX;
            }

            UserPermissions permissions = UserPermissions.NoneSet;
            permissions.SetPaused(groupPreferredPermissions.IsPaused);
            permissions.SetDisableAnimations(groupPreferredPermissions.DisableAnimations);
            permissions.SetDisableSounds(groupPreferredPermissions.DisableSounds);
            permissions.SetDisableVFX(groupPreferredPermissions.DisableVFX);

            await Clients.Users(affectedGroupPairs
                .Select(k => k.Key))
                .Client_UserUpdateOtherPairPermissions(new(new(UserUID), permissions)).ConfigureAwait(false);

            await Clients.User(UserUID).Client_GroupChangeUserPairPermissions(new GroupPairUserPermissionDto(new(group.Key), new(UserUID), group.Value)).ConfigureAwait(false);
            foreach (var item in affectedGroupPairs.Select(k => k.Key))
            {
                await Clients.User(UserUID).Client_UserUpdateSelfPairPermissions(new(new(item), permissions)).ConfigureAwait(false);
            }

            if (wasPaused == groupPreferredPermissions.IsPaused) continue;

            foreach (var groupUserPair in affectedGroupPairs)
            {
                var groupUserIdent = await GetUserIdent(groupUserPair.Key).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(groupUserIdent) && !groupUserPair.Value.OtherPermissions.IsPaused)
                {
                    // if we changed to paused and other was not paused before, we send offline
                    if (groupPreferredPermissions.IsPaused)
                    {
                        await Clients.User(UserUID).Client_UserSendOffline(new(new(groupUserPair.Key, groupUserPair.Value.Alias))).ConfigureAwait(false);
                        await Clients.User(groupUserPair.Key).Client_UserSendOffline(new(new(UserUID))).ConfigureAwait(false);
                    }
                    // if we changed to unpaused and other was not paused either we send online
                    else
                    {
                        await Clients.User(UserUID).Client_UserSendOnline(new(new(groupUserPair.Key, groupUserPair.Value.Alias), groupUserIdent)).ConfigureAwait(false);
                        await Clients.User(groupUserPair.Key).Client_UserSendOnline(new(new(UserUID), UserCharaIdent)).ConfigureAwait(false);
                    }
                }
            }
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }
}
