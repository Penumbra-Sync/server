﻿using MareSynchronos.API;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Models;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.InvokeGroupCreate)]
    public async Task<GroupCreatedDto> CreateGroup()
    {
        _logger.LogCallInfo(Api.InvokeGroupCreate);
        var existingGroupsByUser = _dbContext.Groups.Count(u => u.OwnerUID == AuthenticatedUserId);
        var existingJoinedGroups = _dbContext.GroupPairs.Count(u => u.GroupUserUID == AuthenticatedUserId);
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
        var sha = SHA256.Create();
        var hashedPw = StringUtils.Sha256String(passwd);

        Group newGroup = new()
        {
            GID = gid,
            HashedPassword = hashedPw,
            InvitesEnabled = true,
            OwnerUID = AuthenticatedUserId
        };

        GroupPair initialPair = new()
        {
            GroupGID = newGroup.GID,
            GroupUserUID = AuthenticatedUserId,
            IsPaused = false,
            IsPinned = true
        };

        await _dbContext.Groups.AddAsync(newGroup).ConfigureAwait(false);
        await _dbContext.GroupPairs.AddAsync(initialPair).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        var self = _dbContext.Users.Single(u => u.UID == AuthenticatedUserId);

        await Clients.User(AuthenticatedUserId).SendAsync(Api.OnGroupChange, new GroupDto()
        {
            GID = newGroup.GID,
            OwnedBy = string.IsNullOrEmpty(self.Alias) ? self.UID : self.Alias,
            IsDeleted = false,
            IsPaused = false,
            InvitesEnabled = true
        }).ConfigureAwait(false);

        _logger.LogCallInfo(Api.InvokeGroupCreate, gid);

        return new GroupCreatedDto()
        {
            GID = newGroup.GID,
            Password = passwd
        };
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.InvokeGroupGetGroups)]
    public async Task<List<GroupDto>> GetGroups()
    {
        _logger.LogCallInfo(Api.InvokeGroupGetGroups);

        var groups = await _dbContext.GroupPairs.Include(g => g.Group).Include(g => g.Group.Owner).Where(g => g.GroupUserUID == AuthenticatedUserId).ToListAsync().ConfigureAwait(false);

        return groups.Select(g => new GroupDto()
        {
            GID = g.GroupGID,
            Alias = g.Group.Alias,
            InvitesEnabled = g.Group.InvitesEnabled,
            OwnedBy = string.IsNullOrEmpty(g.Group.Owner.Alias) ? g.Group.Owner.UID : g.Group.Owner.Alias,
            IsPaused = g.IsPaused
        }).ToList();
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.InvokeGroupGetUsersInGroup)]
    public async Task<List<GroupPairDto>> GetUsersInGroup(string gid)
    {
        _logger.LogCallInfo(Api.InvokeGroupGetUsersInGroup, gid);

        var (inGroup, _) = await TryValidateUserInGroup(gid).ConfigureAwait(false);
        if (!inGroup) return new List<GroupPairDto>();

        var allPairs = await _dbContext.GroupPairs.Include(g => g.GroupUser).Where(g => g.GroupGID == gid && g.GroupUserUID != AuthenticatedUserId).ToListAsync().ConfigureAwait(false);
        return allPairs.Select(p => new GroupPairDto()
        {
            GroupGID = gid,
            IsPaused = p.IsPaused,
            IsRemoved = false,
            UserUID = p.GroupUser.UID,
            UserAlias = p.GroupUser.Alias,
            IsPinned = p.IsPinned,
            IsModerator = p.IsModerator,
        }).ToList();
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendGroupChangeInviteState)]
    public async Task GroupChangeInviteState(string gid, bool enabled)
    {
        _logger.LogCallInfo(Api.SendGroupChangeInviteState, gid, enabled.ToString());

        var (hasRights, group) = await TryValidateOwner(gid).ConfigureAwait(false);
        if (!hasRights) return;

        group.InvitesEnabled = enabled;
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(Api.SendGroupChangeInviteState, gid, enabled.ToString(), "Success");

        var groupPairs = _dbContext.GroupPairs.Where(p => p.GroupGID == gid).Select(p => p.GroupUserUID).ToList();
        await Clients.Users(groupPairs).SendAsync(Api.OnGroupChange, new GroupDto()
        {
            GID = gid,
            InvitesEnabled = enabled,
        }).ConfigureAwait(false);
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendGroupDelete)]
    public async Task GroupDelete(string gid)
    {
        _logger.LogCallInfo(Api.SendGroupDelete, gid);

        var (hasRights, group) = await TryValidateOwner(gid).ConfigureAwait(false);

        _logger.LogCallInfo(Api.SendGroupDelete, gid, "Success");

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == gid).ToListAsync().ConfigureAwait(false);
        _dbContext.RemoveRange(groupPairs);
        _dbContext.Remove(group);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs.Select(g => g.GroupUserUID)).SendAsync(Api.OnGroupChange, new GroupDto()
        {
            GID = group.GID,
            IsDeleted = true,
        }).ConfigureAwait(false);


        await SendGroupDeletedToAll(groupPairs).ConfigureAwait(false);
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.InvokeGroupJoin)]
    public async Task<bool> GroupJoin(string gid, string password)
    {
        _logger.LogCallInfo(Api.InvokeGroupJoin, gid);

        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid || g.Alias == gid).ConfigureAwait(false);
        var existingPair = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == AuthenticatedUserId).ConfigureAwait(false);
        var hashedPw = StringUtils.Sha256String(password);
        var existingUserCount = await _dbContext.GroupPairs.CountAsync(g => g.GroupGID == gid).ConfigureAwait(false);
        var joinedGroups = await _dbContext.GroupPairs.CountAsync(g => g.GroupUserUID == AuthenticatedUserId).ConfigureAwait(false);
        var isBanned = await _dbContext.GroupBans.AnyAsync(g => g.GroupGID == gid && g.BannedUserUID == AuthenticatedUserId).ConfigureAwait(false);

        if (group == null
            || !string.Equals(group.HashedPassword, hashedPw, StringComparison.Ordinal)
            || existingPair != null
            || existingUserCount >= _maxGroupUserCount
            || !group.InvitesEnabled
            || joinedGroups >= _maxJoinedGroupsByUser
            || isBanned)
            return false;

        GroupPair newPair = new()
        {
            GroupGID = group.GID,
            GroupUserUID = AuthenticatedUserId
        };

        await _dbContext.GroupPairs.AddAsync(newPair).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(Api.InvokeGroupJoin, gid, "Success");

        await Clients.User(AuthenticatedUserId).SendAsync(Api.OnGroupChange, new GroupDto()
        {
            GID = group.GID,
            OwnedBy = group.OwnerUID,
            IsDeleted = false,
            IsPaused = false,
            Alias = group.Alias,
            InvitesEnabled = true
        }).ConfigureAwait(false);

        var self = _dbContext.Users.Single(u => u.UID == AuthenticatedUserId);

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == group.GID && p.GroupUserUID != AuthenticatedUserId).ToListAsync().ConfigureAwait(false);
        await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).SendAsync(Api.OnGroupUserChange, new GroupPairDto()
        {
            GroupGID = group.GID,
            IsPaused = false,
            IsRemoved = false,
            UserUID = AuthenticatedUserId,
            UserAlias = self.Alias,
            IsPinned = false,
            IsModerator = false,
        }).ConfigureAwait(false);

        var allUserPairs = await GetAllPairedClientsWithPauseState().ConfigureAwait(false);

        var userIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);
        foreach (var groupUserPair in groupPairs)
        {
            var userPair = allUserPairs.Single(p => string.Equals(p.UID, groupUserPair.GroupUserUID, StringComparison.Ordinal));
            if (userPair.IsDirectlyPaused != PauseInfo.NoConnection) continue;
            if (userPair.IsPausedExcludingGroup(gid) is PauseInfo.Unpaused) continue;
            if (userPair.IsPausedPerGroup is PauseInfo.Paused) continue;

            var groupUserIdent = await _clientIdentService.GetCharacterIdentForUid(groupUserPair.GroupUserUID).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(groupUserIdent))
            {
                await Clients.User(AuthenticatedUserId).SendAsync(Api.OnUserAddOnlinePairedPlayer, groupUserIdent).ConfigureAwait(false);
                await Clients.User(groupUserPair.GroupUserUID).SendAsync(Api.OnUserAddOnlinePairedPlayer, userIdent).ConfigureAwait(false);
            }
        }

        return true;
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendGroupLeave)]
    public async Task GroupLeave(string gid)
    {
        _logger.LogCallInfo(Api.SendGroupLeave, gid);

        var (exists, groupPair) = await TryValidateUserInGroup(gid).ConfigureAwait(false);
        if (!exists) return;

        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == group.GID).ToListAsync().ConfigureAwait(false);
        var groupPairsWithoutSelf = groupPairs.Where(p => !string.Equals(p.GroupUserUID, AuthenticatedUserId, StringComparison.Ordinal)).ToList();

        _dbContext.GroupPairs.Remove(groupPair);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        await Clients.User(AuthenticatedUserId).SendAsync(Api.OnGroupChange, new GroupDto()
        {
            GID = group.GID,
            IsDeleted = true
        }).ConfigureAwait(false);

        bool ownerHasLeft = string.Equals(group.OwnerUID, AuthenticatedUserId, StringComparison.Ordinal);
        if (ownerHasLeft)
        {
            if (!groupPairsWithoutSelf.Any())
            {
                _logger.LogCallInfo(Api.SendGroupLeave, gid, "Deleted");

                _dbContext.Remove(group);
            }
            else
            {
                var groupHasMigrated = await SharedDbFunctions.MigrateOrDeleteGroup(_dbContext, group, groupPairsWithoutSelf, _maxExistingGroupsByUser).ConfigureAwait(false);

                if (groupHasMigrated.Item1)
                {
                    _logger.LogCallInfo(Api.SendGroupLeave, gid, "Migrated", groupHasMigrated.Item2);

                    await Clients.Users(groupPairsWithoutSelf.Select(p => p.GroupUserUID)).SendAsync(Api.OnGroupChange, new GroupDto()
                    {
                        GID = group.GID,
                        OwnedBy = groupHasMigrated.Item2,
                        Alias = null
                    }).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogCallInfo(Api.SendGroupLeave, gid, "Deleted");

                    await Clients.Users(groupPairsWithoutSelf.Select(p => p.GroupUserUID)).SendAsync(Api.OnGroupChange, new GroupDto()
                    {
                        GID = group.GID,
                        IsDeleted = true
                    }).ConfigureAwait(false);

                    await SendGroupDeletedToAll(groupPairs).ConfigureAwait(false);

                    return;
                }
            }
        }

        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(Api.SendGroupLeave, gid, "Success");

        await Clients.Users(groupPairsWithoutSelf.Select(p => p.GroupUserUID)).SendAsync(Api.OnGroupUserChange, new GroupPairDto()
        {
            GroupGID = group.GID,
            IsRemoved = true,
            UserUID = AuthenticatedUserId,
        }).ConfigureAwait(false);

        var allUserPairs = await GetAllPairedClientsWithPauseState().ConfigureAwait(false);

        var userIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);
        foreach (var groupUserPair in groupPairsWithoutSelf)
        {
            await UserGroupLeave(groupUserPair, allUserPairs, userIdent).ConfigureAwait(false);
        }
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendGroupPause)]
    public async Task GroupChangePauseState(string gid, bool isPaused)
    {
        _logger.LogCallInfo(Api.SendGroupPause, gid, isPaused);

        var (exists, groupPair) = await TryValidateUserInGroup(gid).ConfigureAwait(false);
        if (!exists) return;

        groupPair.IsPaused = isPaused;
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(Api.SendGroupPause, gid, isPaused, "Success");

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == gid && p.GroupUserUID != AuthenticatedUserId).ToListAsync().ConfigureAwait(false);
        await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).SendAsync(Api.OnGroupUserChange, new GroupPairDto()
        {
            GroupGID = gid,
            IsPaused = isPaused,
            UserUID = AuthenticatedUserId,
        }).ConfigureAwait(false);

        await Clients.User(AuthenticatedUserId).SendAsync(Api.OnGroupChange, new GroupDto
        {
            GID = gid,
            IsPaused = isPaused
        }).ConfigureAwait(false);

        var allUserPairs = await GetAllPairedClientsWithPauseState().ConfigureAwait(false);

        var userIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);
        foreach (var groupUserPair in groupPairs)
        {
            var userPair = allUserPairs.SingleOrDefault(p => string.Equals(p.UID, groupUserPair.GroupUserUID, StringComparison.Ordinal));
            if (userPair != null)
            {
                if (userPair.IsDirectlyPaused != PauseInfo.NoConnection) continue;
                if (userPair.IsPausedExcludingGroup(gid) is PauseInfo.Unpaused) continue;
            }

            var groupUserIdent = await _clientIdentService.GetCharacterIdentForUid(groupUserPair.GroupUserUID).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(groupUserIdent))
            {
                await Clients.User(AuthenticatedUserId).SendAsync(isPaused ? Api.OnUserRemoveOnlinePairedPlayer : Api.OnUserAddOnlinePairedPlayer, groupUserIdent).ConfigureAwait(false);
                await Clients.User(groupUserPair.GroupUserUID).SendAsync(isPaused ? Api.OnUserRemoveOnlinePairedPlayer : Api.OnUserAddOnlinePairedPlayer, userIdent).ConfigureAwait(false);
            }
        }
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendGroupRemoveUser)]
    public async Task GroupRemoveUser(string gid, string uid)
    {
        _logger.LogCallInfo(Api.SendGroupRemoveUser, gid, uid);

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(gid).ConfigureAwait(false);
        if (!hasRights) return;

        var (userExists, groupPair) = await TryValidateUserInGroup(gid, uid).ConfigureAwait(false);
        if (!userExists) return;

        _logger.LogCallInfo(Api.SendGroupRemoveUser, gid, uid, "Success");

        _dbContext.GroupPairs.Remove(groupPair);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupPairs = _dbContext.GroupPairs.Where(p => p.GroupGID == group.GID).ToList();
        await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).SendAsync(Api.OnGroupUserChange, new GroupPairDto()
        {
            GroupGID = group.GID,
            IsRemoved = true,
            UserUID = uid,
        }).ConfigureAwait(false);

        var userIdent = await _clientIdentService.GetCharacterIdentForUid(uid).ConfigureAwait(false);
        if (userIdent == null) return;

        await Clients.User(uid).SendAsync(Api.OnGroupChange, new GroupDto()
        {
            GID = gid,
            IsDeleted = true,
        }).ConfigureAwait(false);

        var allUserPairs = await GetAllPairedClientsWithPauseState(uid).ConfigureAwait(false);

        foreach (var groupUserPair in groupPairs)
        {
            await UserGroupLeave(groupUserPair, allUserPairs, userIdent, uid).ConfigureAwait(false);
        }
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendBanUserFromGroup)]
    public async Task GroupBanUser(string gid, string uid, string reason)
    {
        _logger.LogCallInfo(Api.SendBanUserFromGroup, gid, uid);

        var (userHasRights, _) = await TryValidateGroupModeratorOrOwner(gid).ConfigureAwait(false);
        if (!userHasRights) return;

        var (userExists, groupPair) = await TryValidateUserInGroup(gid, uid).ConfigureAwait(false);
        if (!userExists) return;

        var alias = string.IsNullOrEmpty(groupPair.GroupUser.Alias) ? "-" : groupPair.GroupUser.Alias;
        var ban = new GroupBan()
        {
            BannedByUID = AuthenticatedUserId,
            BannedReason = $"{reason} (Alias at time of ban: {alias})",
            BannedOn = DateTime.UtcNow,
            BannedUserUID = uid,
            GroupGID = gid,
        };

        _dbContext.Add(ban);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        await GroupRemoveUser(gid, uid).ConfigureAwait(false);

        _logger.LogCallInfo(Api.SendBanUserFromGroup, gid, uid, "Success");
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendUnbanUserFromGroup)]
    public async Task GroupUnbanUser(string gid, string uid)
    {
        _logger.LogCallInfo(Api.SendUnbanUserFromGroup, gid, uid);

        var (userHasRights, _) = await TryValidateGroupModeratorOrOwner(gid).ConfigureAwait(false);
        if (!userHasRights) return;

        var banEntry = await _dbContext.GroupBans.SingleOrDefaultAsync(g => g.GroupGID == gid && g.BannedUserUID == uid).ConfigureAwait(false);
        if (banEntry == null) return;

        _dbContext.Remove(banEntry);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(Api.SendUnbanUserFromGroup, gid, uid, "Success");
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.InvokeGetBannedUsersForGroup)]
    public async Task<List<BannedGroupUserDto>> GetGroupBannedUsers(string gid)
    {
        _logger.LogCallInfo(Api.InvokeGetBannedUsersForGroup, gid);

        var (userHasRights, _) = await TryValidateGroupModeratorOrOwner(gid).ConfigureAwait(false);
        if (!userHasRights) return new List<BannedGroupUserDto>();

        var banEntries = await _dbContext.GroupBans.Where(g => g.GroupGID == gid).ToListAsync().ConfigureAwait(false);

        List<BannedGroupUserDto> bannedGroupUsers = banEntries.Select(b => new BannedGroupUserDto()
        {
            BannedBy = b.BannedByUID,
            BannedOn = b.BannedOn,
            Reason = b.BannedReason,
            UID = b.BannedUserUID,

        }).ToList();

        _logger.LogCallInfo(Api.InvokeGetBannedUsersForGroup, gid, bannedGroupUsers.Count);

        return bannedGroupUsers;
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendGroupSetModerator)]
    public async Task SetModerator(string gid, string uid, bool isModerator)
    {
        _logger.LogCallInfo(Api.SendGroupSetModerator, gid, uid, IsModerator);

        var (userHasRights, _) = await TryValidateOwner(gid).ConfigureAwait(false);
        if (!userHasRights) return;

        var (userExists, userPair) = await TryValidateUserInGroup(gid, uid).ConfigureAwait(false);
        if (!userExists) return;

        userPair.IsModerator = IsModerator;
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupPairs = await _dbContext.GroupPairs.Where(g => g.GroupGID == gid).ToListAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs.Select(g => g.GroupUserUID)).SendAsync(Api.OnGroupUserChange, new GroupPairDto()
        {
            GroupGID = gid,
            IsModerator = isModerator,
            UserUID = uid
        }).ConfigureAwait(false);

        _logger.LogCallInfo(Api.SendGroupSetModerator, gid, uid, IsModerator, "Success");
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendGroupChangeOwner)]
    public async Task ChangeOwnership(string gid, string uid)
    {
        _logger.LogCallInfo(Api.SendGroupChangeOwner, gid, uid);

        var (isOwner, group) = await TryValidateOwner(gid).ConfigureAwait(false);
        if (!isOwner) return;

        var (isInGroup, newOwnerPair) = await TryValidateUserInGroup(gid, uid).ConfigureAwait(false);
        if (!isInGroup) return;

        var ownedShells = await _dbContext.Groups.CountAsync(g => g.OwnerUID == uid).ConfigureAwait(false);
        if (ownedShells >= _maxExistingGroupsByUser) return;

        var prevOwner = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == AuthenticatedUserId).ConfigureAwait(false);
        prevOwner.IsPinned = false;
        group.Owner = newOwnerPair.GroupUser;
        group.Alias = null;
        newOwnerPair.IsPinned = true;
        newOwnerPair.IsModerator = false;
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(Api.SendGroupChangeOwner, gid, uid, "Success");

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == gid).Select(p => p.GroupUserUID).ToListAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs).SendAsync(Api.OnGroupChange, new GroupDto()
        {
            GID = gid,
            OwnedBy = string.IsNullOrEmpty(group.Owner.Alias) ? group.Owner.UID : group.Owner.Alias,
            Alias = null
        }).ConfigureAwait(false);

        await Clients.Users(groupPairs.Where(p => !string.Equals(p, uid, StringComparison.Ordinal))).SendAsync(Api.OnGroupUserChange, new GroupPairDto()
        {
            GroupGID = gid,
            UserUID = uid,
            IsPinned = true,
            IsModerator = false
        }).ConfigureAwait(false);
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.InvokeGroupChangePassword)]
    public async Task<bool> ChangeGroupPassword(string gid, string password)
    {
        _logger.LogCallInfo(Api.InvokeGroupChangePassword, gid);

        var (isOwner, group) = await TryValidateOwner(gid).ConfigureAwait(false);
        if (!isOwner || password.Length < 10) return false;

        _logger.LogCallInfo(Api.InvokeGroupChangePassword, gid, "Success");

        group.HashedPassword = StringUtils.Sha256String(password);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        return true;
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendGroupChangePinned)]
    public async Task ChangePinned(string gid, string uid, bool isPinned)
    {
        _logger.LogCallInfo(Api.SendGroupChangePinned, gid, uid, isPinned);

        var (userHasRights, _) = await TryValidateGroupModeratorOrOwner(gid).ConfigureAwait(false);
        if (!userHasRights) return;

        var (userInGroup, groupPair) = await TryValidateUserInGroup(gid, uid).ConfigureAwait(false);
        if (!userInGroup) return;

        groupPair.IsPinned = isPinned;
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(Api.InvokeGroupChangePassword, gid, uid, isPinned, "Success");

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == gid).Select(p => p.GroupUserUID).ToListAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs.Where(p => !string.Equals(p, uid, StringComparison.Ordinal))).SendAsync(Api.OnGroupUserChange, new GroupPairDto()
        {
            GroupGID = gid,
            UserUID = uid,
            IsPinned = isPinned
        }).ConfigureAwait(false);
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendGroupClear)]
    public async Task ClearGroup(string gid)
    {
        _logger.LogCallInfo(Api.SendGroupClear, gid);

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(gid).ConfigureAwait(false);
        if (!hasRights) return;

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == gid).ToListAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs.Where(p => !p.IsPinned && !p.IsModerator).Select(g => g.GroupUserUID)).SendAsync(Api.OnGroupChange, new GroupDto()
        {
            GID = group.GID,
            IsDeleted = true,
        }).ConfigureAwait(false);

        _logger.LogCallInfo(Api.SendGroupClear, gid, "Success");

        var notPinned = groupPairs.Where(g => !g.IsPinned).ToList();

        _dbContext.GroupPairs.RemoveRange(notPinned);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        foreach (var pair in notPinned)
        {
            await Clients.Users(groupPairs.Where(p => p.IsPinned).Select(g => g.GroupUserUID)).SendAsync(Api.OnGroupUserChange, new GroupPairDto()
            {
                GroupGID = pair.GroupGID,
                IsRemoved = true,
                UserUID = pair.GroupUserUID
            }).ConfigureAwait(false);

            var pairIdent = await _clientIdentService.GetCharacterIdentForUid(pair.GroupUserUID).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pairIdent)) continue;

            var allUserPairs = await GetAllPairedClientsWithPauseState(pair.GroupUserUID).ConfigureAwait(false);

            foreach (var groupUserPair in groupPairs.Where(p => !string.Equals(p.GroupUserUID, pair.GroupUserUID, StringComparison.Ordinal)))
            {
                await UserGroupLeave(groupUserPair, allUserPairs, pairIdent).ConfigureAwait(false);
            }
        }
    }
}
