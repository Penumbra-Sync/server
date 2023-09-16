﻿using MareSynchronos.API.Data.Enum;
using MareSynchronos.API.Data.Extensions;
using MareSynchronos.API.Dto.Group;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Models;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    // needed, nothing to be done
    [Authorize(Policy = "Identified")]
    public async Task GroupBanUser(GroupPairDto dto, string reason)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto, reason));

        var (userHasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!userHasRights) return;

        var (userExists, groupPair) = await TryValidateUserInGroup(dto.Group.GID, dto.User.UID).ConfigureAwait(false);
        if (!userExists) return;

        if (groupPair.IsModerator || string.Equals(group.OwnerUID, dto.User.UID, StringComparison.Ordinal)) return;

        var alias = string.IsNullOrEmpty(groupPair.GroupUser.Alias) ? "-" : groupPair.GroupUser.Alias;
        var ban = new GroupBan()
        {
            BannedByUID = UserUID,
            BannedReason = $"{reason} (Alias at time of ban: {alias})",
            BannedOn = DateTime.UtcNow,
            BannedUserUID = dto.User.UID,
            GroupGID = dto.Group.GID,
        };

        _dbContext.Add(ban);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        await GroupRemoveUser(dto).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupChangeGroupPermissionState(GroupPermissionDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return;

        group.InvitesEnabled = !dto.Permissions.HasFlag(GroupPermissions.DisableInvites);
        group.PreferDisableSounds = dto.Permissions.HasFlag(GroupPermissions.PreferDisableSounds);
        group.PreferDisableAnimations = dto.Permissions.HasFlag(GroupPermissions.PreferDisableAnimations);
        group.PreferDisableVFX = dto.Permissions.HasFlag(GroupPermissions.PreferDisableVFX);

        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupPairs = _dbContext.GroupPairs.Where(p => p.GroupGID == dto.Group.GID).Select(p => p.GroupUserUID).ToList();
        await Clients.Users(groupPairs).Client_GroupChangePermissions(new GroupPermissionDto(dto.Group, dto.Permissions)).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupChangeIndividualPermissionState(GroupPairUserPermissionDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (inGroup, groupPair) = await TryValidateUserInGroup(dto.Group.GID).ConfigureAwait(false);
        if (!inGroup) return;

        var groupPreferredPermissions = await _dbContext.GroupPairPreferredPermissions.SingleAsync(u => u.UserUID == UserUID).ConfigureAwait(false);

        var wasPaused = groupPreferredPermissions.IsPaused;
        groupPreferredPermissions.DisableSounds = dto.GroupPairPermissions.IsDisableSounds();
        groupPreferredPermissions.DisableAnimations = dto.GroupPairPermissions.IsDisableAnimations();
        groupPreferredPermissions.IsPaused = dto.GroupPairPermissions.IsPaused();
        groupPreferredPermissions.DisableVFX = dto.GroupPairPermissions.IsDisableVFX();

        // set the permissions for every group pair
        var allPairs = await (await GetAllPairsWithPermissions(UserUID).ConfigureAwait(false))
            .Where(u => !u.OwnPermissions.Sticky).ToListAsync().ConfigureAwait(false);
        var affectedGroupPairs = allPairs.Where(u => string.Equals(u.OtherUser.GID, dto.GID, StringComparison.Ordinal)).ToList();
        await _dbContext.Permissions.Where(u => u.UserUID == UserUID
            && affectedGroupPairs.Select(k => k.OtherUser.UID).Contains(u.OtherUserUID, StringComparer.OrdinalIgnoreCase))
            .ForEachAsync(u =>
            {
                u.DisableSounds = groupPreferredPermissions.DisableSounds;
                u.DisableAnimations = groupPreferredPermissions.DisableAnimations;
                u.IsPaused = groupPreferredPermissions.IsPaused;
                u.DisableVFX = groupPreferredPermissions.DisableVFX;
            }).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        // send messages
        UserPermissions pairedPermissions = UserPermissions.Paired;
        pairedPermissions.SetPaused(groupPreferredPermissions.IsPaused);
        pairedPermissions.SetDisableAnimations(groupPreferredPermissions.DisableAnimations);
        pairedPermissions.SetDisableSounds(groupPreferredPermissions.DisableSounds);
        pairedPermissions.SetDisableVFX(groupPreferredPermissions.DisableVFX);
        UserPermissions unpairedPermissions = pairedPermissions;
        unpairedPermissions.SetPaired(false);

        // send apporpriate permission set to each user
        await Clients.Users(affectedGroupPairs.Where(u => !allPairs.Exists(e =>
            string.Equals(e.OtherUser.UID, u.OtherUser.UID, StringComparison.Ordinal)
            && string.Equals(e.OtherUser.GID, "DIRECT", StringComparison.Ordinal))).Select(k => k.OtherUser.UID))
            .Client_UserUpdateOtherPairPermissions(new(new(UserUID), unpairedPermissions)).ConfigureAwait(false);
        await Clients.Users(affectedGroupPairs.Where(u => allPairs.Exists(e =>
            string.Equals(e.OtherUser.UID, u.OtherUser.UID, StringComparison.Ordinal)
            && string.Equals(e.OtherUser.GID, "DIRECT", StringComparison.Ordinal))).Select(k => k.OtherUser.UID))
            .Client_UserUpdateOtherPairPermissions(new(new(UserUID), pairedPermissions)).ConfigureAwait(false);

        var self = await _dbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);

        if (wasPaused == groupPreferredPermissions.IsPaused) return;

        foreach (var groupUserPair in affectedGroupPairs)
        {
            var groupUserIdent = await GetUserIdent(groupUserPair.OtherUser.UID).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(groupUserIdent))
            {
                // if we changed to paused and other was not paused before, we send offline
                if (groupPreferredPermissions.IsPaused && !groupUserPair.OtherPermissions.IsPaused)
                {
                    await Clients.User(UserUID).Client_UserSendOffline(new(new(groupUserPair.OtherUser.UID, groupUserPair.OtherUser.Alias))).ConfigureAwait(false);
                    await Clients.User(groupUserPair.OtherUser.UID)
                        .Client_UserSendOffline(new(self.ToUserData())).ConfigureAwait(false);
                }
                // if we changed to unpaused and other was not paused either we send online
                else if (!groupPreferredPermissions.IsPaused && !groupUserPair.OtherPermissions.IsPaused)
                {
                    await Clients.User(UserUID).Client_UserSendOnline(new(new(groupUserPair.OtherUser.UID, groupUserPair.OtherUser.Alias), groupUserIdent)).ConfigureAwait(false);
                    await Clients.User(groupUserPair.OtherUser.UID)
                        .Client_UserSendOnline(new(self.ToUserData(), UserCharaIdent)).ConfigureAwait(false);
                }
            }
        }
    }

    // needed, no changes
    [Authorize(Policy = "Identified")]
    public async Task GroupChangeOwnership(GroupPairDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (isOwner, group) = await TryValidateOwner(dto.Group.GID).ConfigureAwait(false);
        if (!isOwner) return;

        var (isInGroup, newOwnerPair) = await TryValidateUserInGroup(dto.Group.GID, dto.User.UID).ConfigureAwait(false);
        if (!isInGroup) return;

        var ownedShells = await _dbContext.Groups.CountAsync(g => g.OwnerUID == dto.User.UID).ConfigureAwait(false);
        if (ownedShells >= _maxExistingGroupsByUser) return;

        var prevOwner = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == dto.Group.GID && g.GroupUserUID == UserUID).ConfigureAwait(false);
        prevOwner.IsPinned = false;
        group.Owner = newOwnerPair.GroupUser;
        group.Alias = null;
        newOwnerPair.IsPinned = true;
        newOwnerPair.IsModerator = false;
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == dto.Group.GID).Select(p => p.GroupUserUID).AsNoTracking().ToListAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs).Client_GroupSendInfo(new GroupInfoDto(group.ToGroupData(), newOwnerPair.GroupUser.ToUserData(), group.GetGroupPermissions())).ConfigureAwait(false);
    }

    // needed, no changes
    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupChangePassword(GroupPasswordDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (isOwner, group) = await TryValidateOwner(dto.Group.GID).ConfigureAwait(false);
        if (!isOwner || dto.Password.Length < 10) return false;

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        group.HashedPassword = StringUtils.Sha256String(dto.Password);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        return true;
    }

    // todo: needed, but need to change the all paired clients stuff
    [Authorize(Policy = "Identified")]
    public async Task GroupClear(GroupDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return;

        var groupPairs = await _dbContext.GroupPairs.Include(p => p.GroupUser).Where(p => p.GroupGID == dto.Group.GID).ToListAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs.Where(p => !p.IsPinned && !p.IsModerator).Select(g => g.GroupUserUID)).Client_GroupDelete(new GroupDto(group.ToGroupData())).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        var notPinned = groupPairs.Where(g => !g.IsPinned && !g.IsModerator).ToList();

        _dbContext.GroupPairs.RemoveRange(notPinned);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        foreach (var pair in notPinned)
        {
            await Clients.Users(groupPairs.Where(p => p.IsPinned).Select(g => g.GroupUserUID)).Client_GroupPairLeft(new GroupPairDto(dto.Group, pair.GroupUser.ToUserData())).ConfigureAwait(false);

            var pairIdent = await GetUserIdent(pair.GroupUserUID).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pairIdent)) continue;

            var allUserPairs = await GetAllPairedClientsWithPauseState(pair.GroupUserUID).ConfigureAwait(false);

            foreach (var groupUserPair in groupPairs.Where(p => !string.Equals(p.GroupUserUID, pair.GroupUserUID, StringComparison.Ordinal)))
            {
                await UserGroupLeave(groupUserPair, allUserPairs, pairIdent).ConfigureAwait(false);
            }
        }
    }

    // todo: check if it works
    [Authorize(Policy = "Identified")]
    public async Task<GroupPasswordDto> GroupCreate()
    {
        _logger.LogCallInfo();
        var existingGroupsByUser = await _dbContext.Groups.CountAsync(u => u.OwnerUID == UserUID).ConfigureAwait(false);
        var existingJoinedGroups = await _dbContext.GroupPairs.CountAsync(u => u.GroupUserUID == UserUID).ConfigureAwait(false);
        if (existingGroupsByUser >= _maxExistingGroupsByUser || existingJoinedGroups >= _maxJoinedGroupsByUser)
        {
            throw new System.Exception($"Max groups for user is {_maxExistingGroupsByUser}, max joined groups is {_maxJoinedGroupsByUser}.");
        }

        var gid = StringUtils.GenerateRandomString(12);
        while (await _dbContext.Groups.AnyAsync(g => g.GID == "MSS-" + gid).ConfigureAwait(false))
        {
            gid = StringUtils.GenerateRandomString(12);
        }
        gid = "MSS-" + gid;

        var passwd = StringUtils.GenerateRandomString(16);
        using var sha = SHA256.Create();
        var hashedPw = StringUtils.Sha256String(passwd);

        UserDefaultPreferredPermission defaultPermissions = await _dbContext.UserDefaultPreferredPermissions.SingleAsync(u => u.UserUID == UserUID).ConfigureAwait(false);

        Group newGroup = new()
        {
            GID = gid,
            HashedPassword = hashedPw,
            InvitesEnabled = true,
            OwnerUID = UserUID,
            PreferDisableAnimations = defaultPermissions.DisableGroupAnimations,
            PreferDisableSounds = defaultPermissions.DisableGroupSounds,
            PreferDisableVFX = defaultPermissions.DisableGroupVFX
        };

        GroupPair initialPair = new()
        {
            GroupGID = newGroup.GID,
            GroupUserUID = UserUID,
            IsPinned = true,
        };

        GroupPairPreferredPermission initialPrefPermissions = new()
        {
            UserUID = UserUID,
            GroupGID = newGroup.GID,
            DisableSounds = defaultPermissions.DisableGroupSounds,
            DisableAnimations = defaultPermissions.DisableGroupAnimations,
            DisableVFX = defaultPermissions.DisableGroupAnimations
        };

        await _dbContext.Groups.AddAsync(newGroup).ConfigureAwait(false);
        await _dbContext.GroupPairs.AddAsync(initialPair).ConfigureAwait(false);
        await _dbContext.GroupPairPreferredPermissions.AddAsync(initialPrefPermissions).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        var self = await _dbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);

        // todo: send correct permissions
        await Clients.User(UserUID).Client_GroupSendFullInfo(new GroupFullInfoDto(newGroup.ToGroupData(), self.ToUserData(), GroupPermissions.NoneSet, GroupUserPreferredPermissions.NoneSet, GroupUserInfo.None))
            .ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(gid));

        return new GroupPasswordDto(newGroup.ToGroupData(), passwd);
    }

    // needed, no changes
    [Authorize(Policy = "Identified")]
    public async Task<List<string>> GroupCreateTempInvite(GroupDto dto, int amount)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto, amount));
        List<string> inviteCodes = new();
        List<GroupTempInvite> tempInvites = new();
        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return new();

        var existingInvites = await _dbContext.GroupTempInvites.Where(g => g.GroupGID == group.GID).ToListAsync().ConfigureAwait(false);

        for (int i = 0; i < amount; i++)
        {
            bool hasValidInvite = false;
            string invite = string.Empty;
            string hashedInvite = string.Empty;
            while (!hasValidInvite)
            {
                invite = StringUtils.GenerateRandomString(10, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
                hashedInvite = StringUtils.Sha256String(invite);
                if (existingInvites.Any(i => string.Equals(i.Invite, hashedInvite, StringComparison.Ordinal))) continue;
                hasValidInvite = true;
                inviteCodes.Add(invite);
            }

            tempInvites.Add(new GroupTempInvite()
            {
                ExpirationDate = DateTime.UtcNow.AddDays(1),
                GroupGID = group.GID,
                Invite = hashedInvite,
            });
        }

        _dbContext.GroupTempInvites.AddRange(tempInvites);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        return inviteCodes;
    }

    // needed, no changes
    [Authorize(Policy = "Identified")]
    public async Task GroupDelete(GroupDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (hasRights, group) = await TryValidateOwner(dto.Group.GID).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == dto.Group.GID).ToListAsync().ConfigureAwait(false);
        _dbContext.RemoveRange(groupPairs);
        _dbContext.Remove(group);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs.Select(g => g.GroupUserUID)).Client_GroupDelete(new GroupDto(group.ToGroupData())).ConfigureAwait(false);

        await SendGroupDeletedToAll(groupPairs).ConfigureAwait(false);
    }

    // needed, no changes
    [Authorize(Policy = "Identified")]
    public async Task<List<BannedGroupUserDto>> GroupGetBannedUsers(GroupDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (userHasRights, group) = await TryValidateGroupModeratorOrOwner(dto.GID).ConfigureAwait(false);
        if (!userHasRights) return new List<BannedGroupUserDto>();

        var banEntries = await _dbContext.GroupBans.Include(b => b.BannedUser).Where(g => g.GroupGID == dto.Group.GID).AsNoTracking().ToListAsync().ConfigureAwait(false);

        List<BannedGroupUserDto> bannedGroupUsers = banEntries.Select(b =>
            new BannedGroupUserDto(group.ToGroupData(), b.BannedUser.ToUserData(), b.BannedReason, b.BannedOn,
                b.BannedByUID)).ToList();

        _logger.LogCallInfo(MareHubLogger.Args(dto, bannedGroupUsers.Count));

        return bannedGroupUsers;
    }

    // todo: needed, also requires changes for permissions
    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupJoin(GroupPasswordDto dto)
    {
        var aliasOrGid = dto.Group.GID.Trim();

        _logger.LogCallInfo(MareHubLogger.Args(dto.Group));

        var group = await _dbContext.Groups.Include(g => g.Owner).AsNoTracking().SingleOrDefaultAsync(g => g.GID == aliasOrGid || g.Alias == aliasOrGid).ConfigureAwait(false);
        var groupGid = group?.GID ?? string.Empty;
        var existingPair = await _dbContext.GroupPairs.AsNoTracking().SingleOrDefaultAsync(g => g.GroupGID == groupGid && g.GroupUserUID == UserUID).ConfigureAwait(false);
        var hashedPw = StringUtils.Sha256String(dto.Password);
        var existingUserCount = await _dbContext.GroupPairs.AsNoTracking().CountAsync(g => g.GroupGID == groupGid).ConfigureAwait(false);
        var joinedGroups = await _dbContext.GroupPairs.CountAsync(g => g.GroupUserUID == UserUID).ConfigureAwait(false);
        var isBanned = await _dbContext.GroupBans.AnyAsync(g => g.GroupGID == groupGid && g.BannedUserUID == UserUID).ConfigureAwait(false);
        var oneTimeInvite = await _dbContext.GroupTempInvites.SingleOrDefaultAsync(g => g.GroupGID == groupGid && g.Invite == hashedPw).ConfigureAwait(false);

        if (group == null
            || (!string.Equals(group.HashedPassword, hashedPw, StringComparison.Ordinal) && oneTimeInvite == null)
            || existingPair != null
            || existingUserCount >= _maxGroupUserCount
            || !group.InvitesEnabled
            || joinedGroups >= _maxJoinedGroupsByUser
            || isBanned)
            return false;

        if (oneTimeInvite != null)
        {
            _logger.LogCallInfo(MareHubLogger.Args(aliasOrGid, "TempInvite", oneTimeInvite.Invite));
            _dbContext.Remove(oneTimeInvite);
        }

        GroupPair newPair = new()
        {
            GroupGID = group.GID,
            GroupUserUID = UserUID,
            DisableAnimations = false,
            DisableSounds = false,
            DisableVFX = false
        };

        await _dbContext.GroupPairs.AddAsync(newPair).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(aliasOrGid, "Success"));

        await Clients.User(UserUID).Client_GroupSendFullInfo(new GroupFullInfoDto(group.ToGroupData(), group.Owner.ToUserData(), group.GetGroupPermissions(), newPair.GetGroupPairPermissions(), newPair.GetGroupPairUserInfo())).ConfigureAwait(false);

        var self = _dbContext.Users.Single(u => u.UID == UserUID);

        var groupPairs = await _dbContext.GroupPairs.Include(p => p.GroupUser).Where(p => p.GroupGID == group.GID && p.GroupUserUID != UserUID).ToListAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs.Select(p => p.GroupUserUID))
            .Client_GroupPairJoined(new GroupPairFullInfoDto(group.ToGroupData(), self.ToUserData(), newPair.GetGroupPairUserInfo(), newPair.GetGroupPairPermissions())).ConfigureAwait(false);
        foreach (var pair in groupPairs)
        {
            await Clients.User(UserUID).Client_GroupPairJoined(new GroupPairFullInfoDto(group.ToGroupData(), pair.ToUserData(), pair.GetGroupPairUserInfo(), pair.GetGroupPairPermissions())).ConfigureAwait(false);
        }

        var allUserPairs = await GetAllPairedClientsWithPauseState().ConfigureAwait(false);

        foreach (var groupUserPair in groupPairs)
        {
            var userPair = allUserPairs.Single(p => string.Equals(p.UID, groupUserPair.GroupUserUID, StringComparison.Ordinal));
            if (userPair.IsDirectlyPaused != PauseInfo.NoConnection) continue;
            if (userPair.IsPausedExcludingGroup(group.GID) is PauseInfo.Unpaused) continue;
            if (userPair.IsPausedPerGroup is PauseInfo.Paused) continue;

            var groupUserIdent = await GetUserIdent(groupUserPair.GroupUserUID).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(groupUserIdent))
            {
                await Clients.User(UserUID).Client_UserSendOnline(new(groupUserPair.ToUserData(), groupUserIdent)).ConfigureAwait(false);
                await Clients.User(groupUserPair.GroupUserUID)
                    .Client_UserSendOnline(new(self.ToUserData(), UserCharaIdent)).ConfigureAwait(false);
            }
        }

        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupLeave(GroupDto dto)
    {
        await UserLeaveGroup(dto, UserUID).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupRemoveUser(GroupPairDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return;

        var (userExists, groupPair) = await TryValidateUserInGroup(dto.Group.GID, dto.User.UID).ConfigureAwait(false);
        if (!userExists) return;

        if (groupPair.IsModerator || string.Equals(group.OwnerUID, dto.User.UID, StringComparison.Ordinal)) return;
        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        _dbContext.GroupPairs.Remove(groupPair);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupPairs = _dbContext.GroupPairs.Where(p => p.GroupGID == group.GID).AsNoTracking().ToList();
        await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).Client_GroupPairLeft(dto).ConfigureAwait(false);

        var userIdent = await GetUserIdent(dto.User.UID).ConfigureAwait(false);
        if (userIdent == null) return;

        await Clients.User(dto.User.UID).Client_GroupDelete(new GroupDto(dto.Group)).ConfigureAwait(false);

        var allUserPairs = await GetAllPairedClientsWithPauseState(dto.User.UID).ConfigureAwait(false);

        foreach (var groupUserPair in groupPairs)
        {
            await UserGroupLeave(groupUserPair, allUserPairs, userIdent, dto.User.UID).ConfigureAwait(false);
        }
    }

    // needed, no changes
    [Authorize(Policy = "Identified")]
    public async Task GroupSetUserInfo(GroupPairUserInfoDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (userExists, userPair) = await TryValidateUserInGroup(dto.Group.GID, dto.User.UID).ConfigureAwait(false);
        if (!userExists) return;

        var (userIsOwner, _) = await TryValidateOwner(dto.Group.GID).ConfigureAwait(false);
        var (userIsModerator, _) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);

        if (dto.GroupUserInfo.HasFlag(GroupUserInfo.IsPinned) && userIsModerator && !userPair.IsPinned)
        {
            userPair.IsPinned = true;
        }
        else if (userIsModerator && userPair.IsPinned)
        {
            userPair.IsPinned = false;
        }

        if (dto.GroupUserInfo.HasFlag(GroupUserInfo.IsModerator) && userIsOwner && !userPair.IsModerator)
        {
            userPair.IsModerator = true;
        }
        else if (userIsOwner && userPair.IsModerator)
        {
            userPair.IsModerator = false;
        }

        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupPairs = await _dbContext.GroupPairs.AsNoTracking().Where(p => p.GroupGID == dto.Group.GID).Select(p => p.GroupUserUID).ToListAsync().ConfigureAwait(false);
        await Clients.Users(groupPairs).Client_GroupPairChangeUserInfo(new GroupPairUserInfoDto(dto.Group, dto.User, userPair.GetGroupPairUserInfo())).ConfigureAwait(false);
    }

    // todo: check if changes work out
    [Authorize(Policy = "Identified")]
    public async Task<List<GroupFullInfoDto>> GroupsGetAll()
    {
        _logger.LogCallInfo();

        var groups = await _dbContext.GroupPairs.Include(g => g.Group).Include(g => g.Group.Owner).Where(g => g.GroupUserUID == UserUID).AsNoTracking().ToListAsync().ConfigureAwait(false);
        var preferredPermissions = await _dbContext.GroupPairPreferredPermissions.Where(u => groups.Exists(k => k.GroupGID == u.GroupGID) && u.UserUID == UserUID)
            .ToDictionaryAsync(u => groups.First(f => string.Equals(f.GroupGID, u.GroupGID, StringComparison.Ordinal)), u => u).ConfigureAwait(false);

        return preferredPermissions.Select(g => new GroupFullInfoDto(g.Key.Group.ToGroupData(), g.Key.Group.Owner.ToUserData(),
                g.Key.Group.GetGroupPermissions(), g.Value.GetGroupPairPermissions(), g.Key.GetGroupPairUserInfo())).ToList();
    }

    // this is probably not needed anymore
    [Authorize(Policy = "Identified")]
    public async Task<List<GroupPairFullInfoDto>> GroupsGetUsersInGroup(GroupDto dto)
    {
        throw new NotImplementedException();
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (inGroup, _) = await TryValidateUserInGroup(dto.Group.GID).ConfigureAwait(false);
        if (!inGroup) return new List<GroupPairFullInfoDto>();

        var group = await _dbContext.Groups.SingleAsync(g => g.GID == dto.Group.GID).ConfigureAwait(false);
        var allPairs = await _dbContext.GroupPairs.Include(g => g.GroupUser).Where(g => g.GroupGID == dto.Group.GID && g.GroupUserUID != UserUID).AsNoTracking().ToListAsync().ConfigureAwait(false);
        // return allPairs.Select(p => new GroupPairFullInfoDto(group.ToGroupData(), p.GroupUser.ToUserData(), p.GetGroupPairUserInfo(), ())).ToList();
    }

    // needed, no changes
    [Authorize(Policy = "Identified")]
    public async Task GroupUnbanUser(GroupPairDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (userHasRights, _) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!userHasRights) return;

        var banEntry = await _dbContext.GroupBans.SingleOrDefaultAsync(g => g.GroupGID == dto.Group.GID && g.BannedUserUID == dto.User.UID).ConfigureAwait(false);
        if (banEntry == null) return;

        _dbContext.Remove(banEntry);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));
    }
}