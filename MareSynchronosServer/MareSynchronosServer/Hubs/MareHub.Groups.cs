using MareSynchronos.API;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Models;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
        if (existingGroupsByUser >= 3 || existingJoinedGroups >= 6)
        {
            throw new System.Exception("Max groups for user is 3, max joined groups is 6");
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
            IsPaused = false
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
        });

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
            UserAlias = p.GroupUser.Alias
        }).ToList();
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendGroupChangeInviteState)]
    public async Task GroupChangeInviteState(string gid, bool enabled)
    {
        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
        if (group == null || group.OwnerUID != AuthenticatedUserId) return;

        group.InvitesEnabled = enabled;
        await _dbContext.SaveChangesAsync();

        var groupPairs = _dbContext.GroupPairs.Where(p => p.GroupGID == gid).Select(p => p.GroupUserUID).ToList();
        await Clients.Users(groupPairs).SendAsync(Api.OnGroupChange, new GroupDto()
        {
            GID = gid,
            InvitesEnabled = enabled,
        });
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendGroupDelete)]
    public async Task GroupDelete(string gid)
    {
        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
        if (group == null || group.OwnerUID != AuthenticatedUserId) return;

        _logger.LogInformation("User {uid} deleted {gid}", AuthenticatedUserId, gid);

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == gid).ToListAsync().ConfigureAwait(false);
        await Clients.Users(groupPairs.Select(g => g.GroupUserUID)).SendAsync(Api.OnGroupChange, new GroupDto()
        {
            GID = group.GID,
            IsDeleted = true,
        });

        foreach (var pair in groupPairs)
        {
            var users = await GetUnpausedUsersExcludingGroup(gid, pair.GroupUserUID);
            if (users.Any())
            {
                var groupUserIdent = await _clientIdentService.GetCharacterIdentForUid(pair.GroupUserUID).ConfigureAwait(false);
                await Clients.Users(users).SendAsync(Api.OnUserRemoveOnlinePairedPlayer, groupUserIdent).ConfigureAwait(false);
            }
        }

        _dbContext.RemoveRange(groupPairs);
        _dbContext.Remove(group);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);
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
        if (group == null || group.HashedPassword != hashedPw || existingPair != null || existingUserCount >= 100 || !group.InvitesEnabled || joinedGroups >= 6)
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
            InvitesEnabled = true
        });

        var self = _dbContext.Users.Single(u => u.UID == AuthenticatedUserId);

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == group.GID && p.GroupUserUID != AuthenticatedUserId).ToListAsync().ConfigureAwait(false);
        await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).SendAsync(Api.OnGroupUserChange, new GroupPairDto()
        {
            GroupGID = group.GID,
            IsPaused = false,
            IsRemoved = false,
            UserUID = AuthenticatedUserId,
            UserAlias = self.Alias
        });

        var allUserPairs = await GetAllPairedClientsWithPauseState();

        var groupUsersPausedInAnyOtherGroup = GetGroupUsersPausedInAnyOtherGroup(allUserPairs, groupPairs);

        if (!groupUsersPausedInAnyOtherGroup.Any()) return true;

        var clientIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);
        await Clients.Users(groupUsersPausedInAnyOtherGroup).SendAsync(Api.OnUserAddOnlinePairedPlayer, clientIdent).ConfigureAwait(false);
        foreach (var user in groupUsersPausedInAnyOtherGroup)
        {
            var ident = await _clientIdentService.GetCharacterIdentForUid(user).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(ident))
            {
                await Clients.User(AuthenticatedUserId).SendAsync(Api.OnUserAddOnlinePairedPlayer, ident);
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

        if (group.OwnerUID == AuthenticatedUserId)
        {
            var newOwner = await _dbContext.GroupPairs.FirstOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID != AuthenticatedUserId).ConfigureAwait(false);
            if (newOwner == null)
            {
                _logger.LogInformation("Group {gid} has no new owner, deleting", gid);
                _dbContext.Remove(group);
            }
            else
            {
                _logger.LogInformation("Group {gid} has new owner {uid}", gid, newOwner.GroupUserUID);

                group.OwnerUID = newOwner.GroupUserUID;

                await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).SendAsync(Api.OnGroupChange, new GroupDto()
                {
                    GID = group.GID,
                    OwnedBy = newOwner.GroupUserUID,
                });
            }
        }

        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogInformation("User {user} left group {gid}", AuthenticatedUserId, gid);

        await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).SendAsync(Api.OnGroupUserChange, new GroupPairDto()
        {
            GroupGID = group.GID,
            IsRemoved = true,
            UserUID = AuthenticatedUserId,
        });

        await Clients.User(AuthenticatedUserId).SendAsync(Api.OnGroupChange, new GroupDto()
        {
            GID = group.GID,
            IsDeleted = true
        });

        var allUserPairs = await GetAllPairedClientsWithPauseState();

        var groupUsersNotPausedInAnyOtherGroup = GetGroupUsersNotUnpausedInAnyOtherGroup(allUserPairs, groupPairs);

        if (!groupUsersNotPausedInAnyOtherGroup.Any()) return;

        var clientIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);
        await Clients.Users(groupUsersNotPausedInAnyOtherGroup).SendAsync(Api.OnUserRemoveOnlinePairedPlayer, clientIdent).ConfigureAwait(false);
        foreach (var user in groupUsersNotPausedInAnyOtherGroup)
        {
            var ident = await _clientIdentService.GetCharacterIdentForUid(user).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(ident))
            {
                await Clients.User(AuthenticatedUserId).SendAsync(Api.OnUserRemoveOnlinePairedPlayer, ident);
            }
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

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == gid && p.GroupUserUID != AuthenticatedUserId).ToListAsync();
        await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).SendAsync(Api.OnGroupUserChange, new GroupPairDto()
        {
            GroupGID = gid,
            IsPaused = isPaused,
            UserUID = AuthenticatedUserId,
        });

        await Clients.User(AuthenticatedUserId).SendAsync(Api.OnGroupChange, new GroupDto
        {
            GID = gid,
            IsPaused = isPaused
        });

        var allUserPairs = await GetAllPairedClientsWithPauseState();

        if (isPaused)
        {

            var groupUsersNotPausedInAnyOtherGroup = GetGroupUsersNotUnpausedInAnyOtherGroup(allUserPairs, groupPairs);

            if (groupUsersNotPausedInAnyOtherGroup.Any())
            {
                var clientIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);
                await Clients.Users(groupUsersNotPausedInAnyOtherGroup).SendAsync(Api.OnUserRemoveOnlinePairedPlayer, clientIdent).ConfigureAwait(false);
                foreach (var user in groupUsersNotPausedInAnyOtherGroup)
                {
                    var ident = await _clientIdentService.GetCharacterIdentForUid(user).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(ident))
                    {
                        await Clients.User(AuthenticatedUserId).SendAsync(Api.OnUserRemoveOnlinePairedPlayer, ident);
                    }
                }
            }
        }
        else
        {
            var groupUsersPausedInAnyOtherGroup = GetGroupUsersPausedInAnyOtherGroup(allUserPairs, groupPairs);

            if (groupUsersPausedInAnyOtherGroup.Any())
            {
                var clientIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);
                await Clients.Users(groupUsersPausedInAnyOtherGroup).SendAsync(Api.OnUserAddOnlinePairedPlayer, clientIdent).ConfigureAwait(false);
                foreach (var user in groupUsersPausedInAnyOtherGroup)
                {
                    var ident = await _clientIdentService.GetCharacterIdentForUid(user).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(ident))
                    {
                        await Clients.User(AuthenticatedUserId).SendAsync(Api.OnUserAddOnlinePairedPlayer, ident);
                    }
                }
            }
        }
    }

    private List<string> GetGroupUsersNotUnpausedInAnyOtherGroup(List<PausedEntry> allUserPairs, List<GroupPair> groupPairs)
    {
        return groupPairs
            .Where(g => !allUserPairs.Any(p => p.UID == g.GroupUserUID && p.IsDirectlyPaused != PauseInfo.NoConnection))
            .Where(g => !allUserPairs.Any(p => p.UID == g.GroupUserUID && p.IsPausedExcludingGroup(g.GroupGID) == PauseInfo.Unpaused))
            .Select(p => p.GroupUserUID).ToList();
    }

    private List<string> GetGroupUsersPausedInAnyOtherGroup(List<PausedEntry> allUserPairs, List<GroupPair> groupPairs)
    {
        var groupUsersNotDirectConnected = groupPairs
            .Where(g => !allUserPairs.Any(p => p.UID == g.GroupUserUID && p.IsDirectlyPaused != PauseInfo.NoConnection)).ToList();
        List<string> groupUsersPausedInAnyOtherGroup = new();
        foreach (var item in groupUsersNotDirectConnected)
        {
            foreach (var pair in allUserPairs)
            {
                _logger.LogInformation("{uid} pausedPerGroup: {group} pausedExcluding: {exclude}", pair.UID, pair.IsPausedPerGroup, pair.IsPausedExcludingGroup(item.GroupGID));
                if (pair.UID == item.GroupUserUID && pair.IsPausedPerGroup is PauseInfo.Unpaused && pair.IsPausedExcludingGroup(item.GroupGID) is PauseInfo.Paused or PauseInfo.NoConnection)
                {
                    groupUsersPausedInAnyOtherGroup.Add(item.GroupUserUID);
                    break;
                }
            }
        }

        return groupUsersPausedInAnyOtherGroup;
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendGroupRemoveUser)]
    public async Task GroupRemoveUser(string gid, string uid)
    {
        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
        if (group == null || group.OwnerUID != AuthenticatedUserId) return;
        var groupPair = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == uid).ConfigureAwait(false);
        if (groupPair == null) return;

        _dbContext.GroupPairs.Remove(groupPair);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupPairs = _dbContext.GroupPairs.Where(p => p.GroupGID == group.GID).ToList();
        await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).SendAsync(Api.OnGroupUserChange, new GroupPairDto()
        {
            GroupGID = group.GID,
            IsRemoved = true,
            UserUID = uid,
        });

        var userIdent = await _clientIdentService.GetCharacterIdentForUid(uid).ConfigureAwait(false);
        if (userIdent == null) return;

        await Clients.User(uid).SendAsync(Api.OnGroupChange, new GroupDto()
        {
            GID = gid,
            IsDeleted = true,
        });

        var allUserPairs = await GetAllPairedClientsWithPauseState(uid);

        var groupUsersNotPausedInAnyOtherGroup = GetGroupUsersNotUnpausedInAnyOtherGroup(allUserPairs, groupPairs);

        if (!groupUsersNotPausedInAnyOtherGroup.Any()) return;

        var clientIdent = await _clientIdentService.GetCharacterIdentForUid(uid).ConfigureAwait(false);
        await Clients.Users(groupUsersNotPausedInAnyOtherGroup).SendAsync(Api.OnUserRemoveOnlinePairedPlayer, clientIdent).ConfigureAwait(false);
        foreach (var user in groupUsersNotPausedInAnyOtherGroup)
        {
            var ident = await _clientIdentService.GetCharacterIdentForUid(user).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(ident))
            {
                await Clients.User(uid).SendAsync(Api.OnUserRemoveOnlinePairedPlayer, ident);
            }
        }
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.SendGroupChangeOwner)]
    public async Task ChangeOwnership(string gid, string uid)
    {
        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
        if (group == null || group.OwnerUID != AuthenticatedUserId) return;
        var groupPair = await _dbContext.GroupPairs.Include(g => g.GroupUser).SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == uid).ConfigureAwait(false);
        if (groupPair == null) return;

        group.Owner = groupPair.GroupUser;
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == gid).Select(p => p.GroupUserUID).ToListAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs).SendAsync(Api.OnGroupChange, new GroupDto()
        {
            GID = gid,
            OwnedBy = string.IsNullOrEmpty(group.Owner.Alias) ? group.Owner.UID : group.Owner.Alias,
        });
    }

    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    [HubMethodName(Api.InvokeGroupChangePassword)]
    public async Task<bool> ChangeGroupPassword(string gid, string password)
    {
        _logger.LogInformation("User {user} attempts to change password for {gid}", AuthenticatedUserId, gid);
        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
        if (group == null || group.OwnerUID != AuthenticatedUserId) return false;

        if (password.Length < 10) return false;

        _logger.LogInformation("User {user} changed password for {gid}", AuthenticatedUserId, gid);

        group.HashedPassword = StringUtils.Sha256String(password);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        return true;
    }

    private record UserPair
    {
        public string UserUID { get; set; }
        public string OtherUserUID { get; set; }
        public bool UserPausedOther { get; set; }
        public bool OtherPausedUser { get; set; }
    }
}
