using MareSynchronos.API;
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
    [Authorize(Policy = "Identified")]
    public async Task<GroupCreatedDto> GroupCreate()
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
        var sha = SHA256.Create();
        var hashedPw = StringUtils.Sha256String(passwd);

        Group newGroup = new()
        {
            GID = gid,
            HashedPassword = hashedPw,
            InvitesEnabled = true,
            OwnerUID = UserUID
        };

        GroupPair initialPair = new()
        {
            GroupGID = newGroup.GID,
            GroupUserUID = UserUID,
            IsPaused = false,
            IsPinned = true
        };

        await _dbContext.Groups.AddAsync(newGroup).ConfigureAwait(false);
        await _dbContext.GroupPairs.AddAsync(initialPair).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        var self = await _dbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);

        await Clients.User(UserUID).Client_GroupChange(new GroupDto()
        {
            GID = newGroup.GID,
            OwnedBy = string.IsNullOrEmpty(self.Alias) ? self.UID : self.Alias,
            IsDeleted = false,
            IsPaused = false,
            InvitesEnabled = true
        }).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(gid));

        return new GroupCreatedDto()
        {
            GID = newGroup.GID,
            Password = passwd
        };
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<GroupDto>> GroupsGetAll()
    {
        _logger.LogCallInfo();

        var groups = await _dbContext.GroupPairs.Include(g => g.Group).Include(g => g.Group.Owner).Where(g => g.GroupUserUID == UserUID).AsNoTracking().ToListAsync().ConfigureAwait(false);

        return groups.Select(g => new GroupDto()
        {
            GID = g.GroupGID,
            Alias = g.Group.Alias,
            InvitesEnabled = g.Group.InvitesEnabled,
            OwnedBy = string.IsNullOrEmpty(g.Group.Owner.Alias) ? g.Group.Owner.UID : g.Group.Owner.Alias,
            IsPaused = g.IsPaused,
            IsModerator = g.IsModerator,
        }).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<GroupPairDto>> GroupsGetUsersInGroup(string gid)
    {
        _logger.LogCallInfo(MareHubLogger.Args(gid));

        var (inGroup, _) = await TryValidateUserInGroup(gid).ConfigureAwait(false);
        if (!inGroup) return new List<GroupPairDto>();

        var allPairs = await _dbContext.GroupPairs.Include(g => g.GroupUser).Where(g => g.GroupGID == gid && g.GroupUserUID != UserUID).AsNoTracking().ToListAsync().ConfigureAwait(false);
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

    [Authorize(Policy = "Identified")]
    public async Task GroupChangeInviteState(string gid, bool enabled)
    {
        _logger.LogCallInfo(MareHubLogger.Args(gid, enabled.ToString()));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(gid).ConfigureAwait(false);
        if (!hasRights) return;

        group.InvitesEnabled = enabled;
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(gid, enabled.ToString(), "Success"));

        var groupPairs = _dbContext.GroupPairs.Where(p => p.GroupGID == gid).Select(p => p.GroupUserUID).ToList();
        await Clients.Users(groupPairs).Client_GroupChange(new GroupDto()
        {
            GID = gid,
            InvitesEnabled = enabled,
        }).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupDelete(string gid)
    {
        _logger.LogCallInfo(MareHubLogger.Args(gid));

        var (hasRights, group) = await TryValidateOwner(gid).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(gid, "Success"));

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == gid).ToListAsync().ConfigureAwait(false);
        _dbContext.RemoveRange(groupPairs);
        _dbContext.Remove(group);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs.Select(g => g.GroupUserUID)).Client_GroupChange(new GroupDto()
        {
            GID = group.GID,
            IsDeleted = true,
        }).ConfigureAwait(false);


        await SendGroupDeletedToAll(groupPairs).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupJoin(string gid, string password)
    {
        _logger.LogCallInfo(MareHubLogger.Args(gid));

        var group = await _dbContext.Groups.Include(g => g.Owner).AsNoTracking().SingleOrDefaultAsync(g => g.GID == gid || g.Alias == gid).ConfigureAwait(false);
        var existingPair = await _dbContext.GroupPairs.AsNoTracking().SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == UserUID).ConfigureAwait(false);
        var hashedPw = StringUtils.Sha256String(password);
        var existingUserCount = await _dbContext.GroupPairs.AsNoTracking().CountAsync(g => g.GroupGID == gid).ConfigureAwait(false);
        var joinedGroups = await _dbContext.GroupPairs.CountAsync(g => g.GroupUserUID == UserUID).ConfigureAwait(false);
        var isBanned = await _dbContext.GroupBans.AnyAsync(g => g.GroupGID == gid && g.BannedUserUID == UserUID).ConfigureAwait(false);
        var groupGid = group?.GID ?? string.Empty;
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
            _logger.LogCallInfo(MareHubLogger.Args(gid, "TempInvite", oneTimeInvite.Invite));
            _dbContext.Remove(oneTimeInvite);
        }

        GroupPair newPair = new()
        {
            GroupGID = group.GID,
            GroupUserUID = UserUID
        };

        await _dbContext.GroupPairs.AddAsync(newPair).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(gid, "Success"));

        await Clients.User(UserUID).Client_GroupChange(new GroupDto()
        {
            GID = group.GID,
            OwnedBy = string.IsNullOrEmpty(group.Owner.Alias) ? group.Owner.UID : group.Owner.Alias,
            IsDeleted = false,
            IsPaused = false,
            Alias = group.Alias,
            InvitesEnabled = true
        }).ConfigureAwait(false);

        var self = _dbContext.Users.Single(u => u.UID == UserUID);

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == group.GID && p.GroupUserUID != UserUID).ToListAsync().ConfigureAwait(false);
        await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).Client_GroupUserChange(new GroupPairDto()
        {
            GroupGID = group.GID,
            IsPaused = false,
            IsRemoved = false,
            UserUID = UserUID,
            UserAlias = self.Alias,
            IsPinned = false,
            IsModerator = false,
        }).ConfigureAwait(false);

        var allUserPairs = await GetAllPairedClientsWithPauseState().ConfigureAwait(false);

        foreach (var groupUserPair in groupPairs)
        {
            var userPair = allUserPairs.Single(p => string.Equals(p.UID, groupUserPair.GroupUserUID, StringComparison.Ordinal));
            if (userPair.IsDirectlyPaused != PauseInfo.NoConnection) continue;
            if (userPair.IsPausedExcludingGroup(gid) is PauseInfo.Unpaused) continue;
            if (userPair.IsPausedPerGroup is PauseInfo.Paused) continue;

            var groupUserIdent = _clientIdentService.GetCharacterIdentForUid(groupUserPair.GroupUserUID);
            if (!string.IsNullOrEmpty(groupUserIdent))
            {
                await Clients.User(UserUID).Client_UserChangePairedPlayer(groupUserIdent, true).ConfigureAwait(false);
                await Clients.User(groupUserPair.GroupUserUID).Client_UserChangePairedPlayer(UserCharaIdent, true).ConfigureAwait(false);
            }
        }

        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<string>> GroupCreateTempInvite(string gid, int amount)
    {
        _logger.LogCallInfo(MareHubLogger.Args(gid, amount));
        List<string> inviteCodes = new();
        List<GroupTempInvite> tempInvites = new();
        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(gid).ConfigureAwait(false);
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
                Invite = hashedInvite
            });
        }

        _dbContext.GroupTempInvites.AddRange(tempInvites);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        return inviteCodes;
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupLeave(string gid)
    {
        _logger.LogCallInfo(MareHubLogger.Args(gid));

        var (exists, groupPair) = await TryValidateUserInGroup(gid).ConfigureAwait(false);
        if (!exists) return;

        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == group.GID).ToListAsync().ConfigureAwait(false);
        var groupPairsWithoutSelf = groupPairs.Where(p => !string.Equals(p.GroupUserUID, UserUID, StringComparison.Ordinal)).ToList();

        _dbContext.GroupPairs.Remove(groupPair);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        await Clients.User(UserUID).Client_GroupChange(new GroupDto()
        {
            GID = group.GID,
            IsDeleted = true
        }).ConfigureAwait(false);

        bool ownerHasLeft = string.Equals(group.OwnerUID, UserUID, StringComparison.Ordinal);
        if (ownerHasLeft)
        {
            if (!groupPairsWithoutSelf.Any())
            {
                _logger.LogCallInfo(MareHubLogger.Args(gid, "Deleted"));

                _dbContext.Groups.Remove(group);
            }
            else
            {
                var groupHasMigrated = await SharedDbFunctions.MigrateOrDeleteGroup(_dbContext, group, groupPairsWithoutSelf, _maxExistingGroupsByUser).ConfigureAwait(false);

                if (groupHasMigrated.Item1)
                {
                    _logger.LogCallInfo(MareHubLogger.Args(gid, "Migrated", groupHasMigrated.Item2));

                    await Clients.Users(groupPairsWithoutSelf.Select(p => p.GroupUserUID)).Client_GroupChange(new GroupDto()
                    {
                        GID = group.GID,
                        OwnedBy = groupHasMigrated.Item2,
                        Alias = null
                    }).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogCallInfo(MareHubLogger.Args(gid, "Deleted"));

                    await Clients.Users(groupPairsWithoutSelf.Select(p => p.GroupUserUID)).Client_GroupChange(new GroupDto()
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

        _logger.LogCallInfo(MareHubLogger.Args(gid, "Success"));

        await Clients.Users(groupPairsWithoutSelf.Select(p => p.GroupUserUID)).Client_GroupUserChange(new GroupPairDto()
        {
            GroupGID = group.GID,
            IsRemoved = true,
            UserUID = UserUID,
        }).ConfigureAwait(false);

        var allUserPairs = await GetAllPairedClientsWithPauseState().ConfigureAwait(false);

        foreach (var groupUserPair in groupPairsWithoutSelf)
        {
            await UserGroupLeave(groupUserPair, allUserPairs, UserCharaIdent).ConfigureAwait(false);
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupChangePauseState(string gid, bool isPaused)
    {
        _logger.LogCallInfo(MareHubLogger.Args(gid, isPaused));

        var (exists, groupPair) = await TryValidateUserInGroup(gid).ConfigureAwait(false);
        if (!exists) return;

        groupPair.IsPaused = isPaused;
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(gid, isPaused, "Success"));

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == gid && p.GroupUserUID != UserUID).AsNoTracking().ToListAsync().ConfigureAwait(false);
        await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).Client_GroupUserChange(new GroupPairDto()
        {
            GroupGID = gid,
            IsPaused = isPaused,
            UserUID = UserUID,
        }).ConfigureAwait(false);

        await Clients.User(UserUID).Client_GroupChange(new GroupDto
        {
            GID = gid,
            IsPaused = isPaused
        }).ConfigureAwait(false);

        var allUserPairs = await GetAllPairedClientsWithPauseState().ConfigureAwait(false);

        foreach (var groupUserPair in groupPairs)
        {
            var userPair = allUserPairs.SingleOrDefault(p => string.Equals(p.UID, groupUserPair.GroupUserUID, StringComparison.Ordinal));
            if (userPair != null)
            {
                if (userPair.IsDirectlyPaused != PauseInfo.NoConnection) continue;
                if (userPair.IsPausedExcludingGroup(gid) is PauseInfo.Unpaused) continue;
                if (userPair.IsOtherPausedForSpecificGroup(gid) is PauseInfo.Paused) continue;
            }

            var groupUserIdent = _clientIdentService.GetCharacterIdentForUid(groupUserPair.GroupUserUID);
            if (!string.IsNullOrEmpty(groupUserIdent))
            {
                await Clients.User(UserUID).Client_UserChangePairedPlayer(groupUserIdent, !isPaused).ConfigureAwait(false);
                await Clients.User(groupUserPair.GroupUserUID).Client_UserChangePairedPlayer(UserCharaIdent, !isPaused).ConfigureAwait(false);
            }
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupRemoveUser(string gid, string uid)
    {
        _logger.LogCallInfo(MareHubLogger.Args(gid, uid));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(gid).ConfigureAwait(false);
        if (!hasRights) return;

        var (userExists, groupPair) = await TryValidateUserInGroup(gid, uid).ConfigureAwait(false);
        if (!userExists) return;

        if (groupPair.IsModerator || string.Equals(group.OwnerUID, uid, StringComparison.Ordinal)) return;
        _logger.LogCallInfo(MareHubLogger.Args(gid, uid, "Success"));

        _dbContext.GroupPairs.Remove(groupPair);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupPairs = _dbContext.GroupPairs.Where(p => p.GroupGID == group.GID).AsNoTracking().ToList();
        await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).Client_GroupUserChange(new GroupPairDto()
        {
            GroupGID = group.GID,
            IsRemoved = true,
            UserUID = uid,
        }).ConfigureAwait(false);

        var userIdent = _clientIdentService.GetCharacterIdentForUid(uid);
        if (userIdent == null) return;

        await Clients.User(uid).Client_GroupChange(new GroupDto()
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

    [Authorize(Policy = "Identified")]
    public async Task GroupBanUser(string gid, string uid, string reason)
    {
        _logger.LogCallInfo(MareHubLogger.Args(gid, uid));

        var (userHasRights, group) = await TryValidateGroupModeratorOrOwner(gid).ConfigureAwait(false);
        if (!userHasRights) return;

        var (userExists, groupPair) = await TryValidateUserInGroup(gid, uid).ConfigureAwait(false);
        if (!userExists) return;

        if (groupPair.IsModerator || string.Equals(group.OwnerUID, uid, StringComparison.Ordinal)) return;

        var alias = string.IsNullOrEmpty(groupPair.GroupUser.Alias) ? "-" : groupPair.GroupUser.Alias;
        var ban = new GroupBan()
        {
            BannedByUID = UserUID,
            BannedReason = $"{reason} (Alias at time of ban: {alias})",
            BannedOn = DateTime.UtcNow,
            BannedUserUID = uid,
            GroupGID = gid,
        };

        _dbContext.Add(ban);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        await GroupRemoveUser(gid, uid).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(gid, uid, "Success"));
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupUnbanUser(string gid, string uid)
    {
        _logger.LogCallInfo(MareHubLogger.Args(gid, uid));

        var (userHasRights, _) = await TryValidateGroupModeratorOrOwner(gid).ConfigureAwait(false);
        if (!userHasRights) return;

        var banEntry = await _dbContext.GroupBans.SingleOrDefaultAsync(g => g.GroupGID == gid && g.BannedUserUID == uid).ConfigureAwait(false);
        if (banEntry == null) return;

        _dbContext.Remove(banEntry);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(gid, uid, "Success"));
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<BannedGroupUserDto>> GroupGetBannedUsers(string gid)
    {
        _logger.LogCallInfo(MareHubLogger.Args(gid));

        var (userHasRights, _) = await TryValidateGroupModeratorOrOwner(gid).ConfigureAwait(false);
        if (!userHasRights) return new List<BannedGroupUserDto>();

        var banEntries = await _dbContext.GroupBans.Where(g => g.GroupGID == gid).AsNoTracking().ToListAsync().ConfigureAwait(false);

        List<BannedGroupUserDto> bannedGroupUsers = banEntries.Select(b => new BannedGroupUserDto()
        {
            BannedBy = b.BannedByUID,
            BannedOn = b.BannedOn,
            Reason = b.BannedReason,
            UID = b.BannedUserUID,

        }).ToList();

        _logger.LogCallInfo(MareHubLogger.Args(gid, bannedGroupUsers.Count));

        return bannedGroupUsers;
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupSetModerator(string gid, string uid, bool isGroupModerator)
    {
        _logger.LogCallInfo(MareHubLogger.Args(gid, uid, isGroupModerator));

        var (userHasRights, _) = await TryValidateOwner(gid).ConfigureAwait(false);
        if (!userHasRights) return;

        var (userExists, userPair) = await TryValidateUserInGroup(gid, uid).ConfigureAwait(false);
        if (!userExists) return;

        userPair.IsModerator = isGroupModerator;
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupPairs = await _dbContext.GroupPairs.Where(g => g.GroupGID == gid).AsNoTracking().ToListAsync().ConfigureAwait(false);

        await Clients.User(uid).Client_GroupChange(new GroupDto()
        {
            GID = gid,
            IsModerator = isGroupModerator
        }).ConfigureAwait(false);

        await Clients.Users(groupPairs.Where(p => !string.Equals(p.GroupUserUID, uid, StringComparison.Ordinal))
            .Select(g => g.GroupUserUID)).Client_GroupUserChange(new GroupPairDto()
            {
                GroupGID = gid,
                IsModerator = isGroupModerator,
                UserUID = uid
            }).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(gid, uid, isGroupModerator, "Success"));
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupChangeOwnership(string gid, string uid)
    {
        _logger.LogCallInfo(MareHubLogger.Args(gid, uid));

        var (isOwner, group) = await TryValidateOwner(gid).ConfigureAwait(false);
        if (!isOwner) return;

        var (isInGroup, newOwnerPair) = await TryValidateUserInGroup(gid, uid).ConfigureAwait(false);
        if (!isInGroup) return;

        var ownedShells = await _dbContext.Groups.CountAsync(g => g.OwnerUID == uid).ConfigureAwait(false);
        if (ownedShells >= _maxExistingGroupsByUser) return;

        var prevOwner = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == UserUID).ConfigureAwait(false);
        prevOwner.IsPinned = false;
        group.Owner = newOwnerPair.GroupUser;
        group.Alias = null;
        newOwnerPair.IsPinned = true;
        newOwnerPair.IsModerator = false;
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(gid, uid, "Success"));

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == gid).Select(p => p.GroupUserUID).AsNoTracking().ToListAsync().ConfigureAwait(false);

        await Clients.Users(uid).Client_GroupChange(new GroupDto()
        {
            GID = gid,
            OwnedBy = string.IsNullOrEmpty(group.Owner.Alias) ? group.Owner.UID : group.Owner.Alias,
            IsModerator = false,
            Alias = null
        }).ConfigureAwait(false);

        await Clients.Users(groupPairs).Client_GroupChange(new GroupDto()
        {
            GID = gid,
            OwnedBy = string.IsNullOrEmpty(group.Owner.Alias) ? group.Owner.UID : group.Owner.Alias,
            Alias = null
        }).ConfigureAwait(false);

        await Clients.Users(groupPairs.Where(p => !string.Equals(p, uid, StringComparison.Ordinal))).Client_GroupUserChange(new GroupPairDto()
        {
            GroupGID = gid,
            UserUID = uid,
            IsPinned = true,
            IsModerator = false
        }).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupChangePassword(string gid, string password)
    {
        _logger.LogCallInfo(MareHubLogger.Args(gid));

        var (isOwner, group) = await TryValidateOwner(gid).ConfigureAwait(false);
        if (!isOwner || password.Length < 10) return false;

        _logger.LogCallInfo(MareHubLogger.Args(gid, "Success"));

        group.HashedPassword = StringUtils.Sha256String(password);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupChangePinned(string gid, string uid, bool isPinned)
    {
        _logger.LogCallInfo(MareHubLogger.Args(gid, uid, isPinned));

        var (userHasRights, _) = await TryValidateGroupModeratorOrOwner(gid).ConfigureAwait(false);
        if (!userHasRights) return;

        var (userInGroup, groupPair) = await TryValidateUserInGroup(gid, uid).ConfigureAwait(false);
        if (!userInGroup) return;

        groupPair.IsPinned = isPinned;
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(gid, uid, isPinned, "Success"));

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == gid).Select(p => p.GroupUserUID).AsNoTracking().ToListAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs.Where(p => !string.Equals(p, uid, StringComparison.Ordinal))).Client_GroupUserChange(new GroupPairDto()
        {
            GroupGID = gid,
            UserUID = uid,
            IsPinned = isPinned
        }).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupClear(string gid)
    {
        _logger.LogCallInfo(MareHubLogger.Args(gid));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(gid).ConfigureAwait(false);
        if (!hasRights) return;

        var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == gid).ToListAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs.Where(p => !p.IsPinned && !p.IsModerator).Select(g => g.GroupUserUID)).Client_GroupChange(new GroupDto()
        {
            GID = group.GID,
            IsDeleted = true,
        }).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(gid, "Success"));

        var notPinned = groupPairs.Where(g => !g.IsPinned && !g.IsModerator).ToList();

        _dbContext.GroupPairs.RemoveRange(notPinned);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        foreach (var pair in notPinned)
        {
            await Clients.Users(groupPairs.Where(p => p.IsPinned).Select(g => g.GroupUserUID)).Client_GroupUserChange(new GroupPairDto()
            {
                GroupGID = pair.GroupGID,
                IsRemoved = true,
                UserUID = pair.GroupUserUID
            }).ConfigureAwait(false);

            var pairIdent = _clientIdentService.GetCharacterIdentForUid(pair.GroupUserUID);
            if (string.IsNullOrEmpty(pairIdent)) continue;

            var allUserPairs = await GetAllPairedClientsWithPauseState(pair.GroupUserUID).ConfigureAwait(false);

            foreach (var groupUserPair in groupPairs.Where(p => !string.Equals(p.GroupUserUID, pair.GroupUserUID, StringComparison.Ordinal)))
            {
                await UserGroupLeave(groupUserPair, allUserPairs, pairIdent).ConfigureAwait(false);
            }
        }
    }
}
