using MareSynchronos.API;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
        _logger.LogInformation("User {user} creating group", AuthenticatedUserId);
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

        _logger.LogInformation("User {user} created group: {groupid}", AuthenticatedUserId, gid);

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
        _logger.LogInformation("User {user} requesting groups", AuthenticatedUserId);

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
        _logger.LogInformation("User {user} requesting users in group {gid}", AuthenticatedUserId, gid);

        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
        var existingPair = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == AuthenticatedUserId).ConfigureAwait(false);
        if (group == null || existingPair == null) return new List<GroupPairDto>();

        var allPairs = await _dbContext.GroupPairs.Include(g => g.GroupUser).Where(g => g.GroupGID == gid && g.GroupUserUID != AuthenticatedUserId).ToListAsync().ConfigureAwait(false);
        return allPairs.Select(p => new GroupPairDto()
        {
            GroupGID = gid,
            IsPaused = p.IsPaused,
            IsRemoved = false,
            UserUID = p.GroupUser.UID,
            UserAlias = p.GroupUser.Alias,
            IsPinned = p.IsPinned
        }).ToList();
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendGroupChangeInviteState)]
    public async Task GroupChangeInviteState(string gid, bool enabled)
    {
        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
        if (group == null || !string.Equals(group.OwnerUID, AuthenticatedUserId, StringComparison.Ordinal)) return;

        group.InvitesEnabled = enabled;
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogInformation("User {uid} changed group {gid} invite state to {state}", AuthenticatedUserId, gid, enabled);

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
        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
        if (group == null || !string.Equals(group.OwnerUID, AuthenticatedUserId, StringComparison.Ordinal)) return;

        _logger.LogInformation("User {uid} deleted group {gid}", AuthenticatedUserId, gid);

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
        _logger.LogInformation("User {user} attempting to join group {gid}", AuthenticatedUserId, gid);
        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid || g.Alias == gid).ConfigureAwait(false);
        var existingPair = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == AuthenticatedUserId).ConfigureAwait(false);
        var hashedPw = StringUtils.Sha256String(password);
        var existingUserCount = await _dbContext.GroupPairs.CountAsync(g => g.GroupGID == gid).ConfigureAwait(false);
        var joinedGroups = await _dbContext.GroupPairs.CountAsync(g => g.GroupUserUID == AuthenticatedUserId).ConfigureAwait(false);
        if (group == null || !string.Equals(group.HashedPassword, hashedPw, StringComparison.Ordinal) || existingPair != null || existingUserCount >= _maxGroupUserCount || !group.InvitesEnabled || joinedGroups >= _maxJoinedGroupsByUser)
            return false;

        GroupPair newPair = new()
        {
            GroupGID = group.GID,
            GroupUserUID = AuthenticatedUserId
        };

        await _dbContext.GroupPairs.AddAsync(newPair).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogInformation("User {user} successfully joined group {gid}", AuthenticatedUserId, gid);

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
            IsPinned = false
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
        _logger.LogInformation("User {user} leaving group {gid}", AuthenticatedUserId, gid);
        var groupPair = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == AuthenticatedUserId).ConfigureAwait(false);
        if (groupPair == null) return;

        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == group.GID && p.GroupUserUID != AuthenticatedUserId).ToListAsync().ConfigureAwait(false);

        _dbContext.GroupPairs.Remove(groupPair);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        bool ownerHasLeft = string.Equals(group.OwnerUID, AuthenticatedUserId, StringComparison.Ordinal);
        if (ownerHasLeft)
        {
            if (!groupPairs.Any())
            {
                _logger.LogInformation("Group {gid} has no new owner, deleting", gid);
                _dbContext.Remove(group);
            }
            else
            {
                var groupHasMigrated = await SharedDbFunctions.MigrateOrDeleteGroup(_dbContext, group, groupPairs, _maxExistingGroupsByUser).ConfigureAwait(false);

                if (groupHasMigrated.Item1)
                {
                    await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).SendAsync(Api.OnGroupChange, new GroupDto()
                    {
                        GID = group.GID,
                        OwnedBy = groupHasMigrated.Item2,
                        Alias = null
                    }).ConfigureAwait(false);
                }
                else
                {
                    await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).SendAsync(Api.OnGroupChange, new GroupDto()
                    {
                        GID = group.GID,
                        IsDeleted = true
                    }).ConfigureAwait(false);

                    await SendGroupDeletedToAll(groupPairs).ConfigureAwait(false);

                    return;
                }
            }
        }

        _logger.LogInformation("User {user} left group {gid}", AuthenticatedUserId, gid);

        await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).SendAsync(Api.OnGroupUserChange, new GroupPairDto()
        {
            GroupGID = group.GID,
            IsRemoved = true,
            UserUID = AuthenticatedUserId,
        }).ConfigureAwait(false);

        await Clients.User(AuthenticatedUserId).SendAsync(Api.OnGroupChange, new GroupDto()
        {
            GID = group.GID,
            IsDeleted = true
        }).ConfigureAwait(false);

        var allUserPairs = await GetAllPairedClientsWithPauseState().ConfigureAwait(false);

        var userIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);
        foreach (var groupUserPair in groupPairs)
        {
            await UserGroupLeave(groupUserPair, allUserPairs, userIdent).ConfigureAwait(false);
        }
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendGroupPause)]
    public async Task GroupChangePauseState(string gid, bool isPaused)
    {
        var groupPair = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == AuthenticatedUserId).ConfigureAwait(false);
        if (groupPair == null) return;

        groupPair.IsPaused = isPaused;
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

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
        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
        if (group == null || !string.Equals(group.OwnerUID, AuthenticatedUserId, StringComparison.Ordinal)) return;
        var groupPair = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == uid).ConfigureAwait(false);
        if (groupPair == null) return;

        _logger.LogInformation("{id} removed {uid} from {gid}", AuthenticatedUserId, uid, gid);

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
            _logger.LogInformation("Checking for {uid}", groupUserPair.GroupUserUID);
            await UserGroupLeave(groupUserPair, allUserPairs, userIdent, uid).ConfigureAwait(false);
        }
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendGroupChangeOwner)]
    public async Task ChangeOwnership(string gid, string uid)
    {
        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
        if (group == null || !string.Equals(group.OwnerUID, AuthenticatedUserId, StringComparison.Ordinal)) return;
        var groupPair = await _dbContext.GroupPairs.Include(g => g.GroupUser).SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == uid).ConfigureAwait(false);
        if (groupPair == null) return;
        var ownedShells = await _dbContext.Groups.CountAsync(g => g.OwnerUID == uid).ConfigureAwait(false);
        if (ownedShells >= _maxExistingGroupsByUser) return;

        var prevOwner = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == AuthenticatedUserId).ConfigureAwait(false);
        prevOwner.IsPinned = false;
        group.Owner = groupPair.GroupUser;
        group.;
        groupPair.IsPinned = true;
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogInformation("{uid} changed ownership of {gid} to {otheruser}", AuthenticatedUserId, gid, uid);

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
            IsPinned = true
        }).ConfigureAwait(false);
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.InvokeGroupChangePassword)]
    public async Task<bool> ChangeGroupPassword(string gid, string password)
    {
        _logger.LogInformation("User {user} attempts to change password for {gid}", AuthenticatedUserId, gid);
        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
        if (group == null || !string.Equals(group.OwnerUID, AuthenticatedUserId, StringComparison.Ordinal)) return false;

        if (password.Length < 10) return false;

        _logger.LogInformation("User {user} changed password for {gid}", AuthenticatedUserId, gid);

        group.HashedPassword = StringUtils.Sha256String(password);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        return true;
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendGroupChangePinned)]
    public async Task ChangePinned(string gid, string uid, bool isPinned)
    {
        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
        if (group == null || !string.Equals(group.OwnerUID, AuthenticatedUserId, StringComparison.Ordinal)) return;
        var groupPair = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == uid).ConfigureAwait(false);
        if (groupPair == null) return;

        _logger.LogInformation("{id} changed pin status for {uid} in {gid} to {pin}", AuthenticatedUserId, uid, gid, isPinned);
        groupPair.IsPinned = isPinned;
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

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
        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
        if (group == null || !string.Equals(group.OwnerUID, AuthenticatedUserId, StringComparison.Ordinal)) return;

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == gid).ToListAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs.Where(p => !p.IsPinned).Select(g => g.GroupUserUID)).SendAsync(Api.OnGroupChange, new GroupDto()
        {
            GID = group.GID,
            IsDeleted = true,
        }).ConfigureAwait(false);

        _logger.LogInformation("{uid} is clearing group {gid}", AuthenticatedUserId, gid);

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
