using MareSynchronos.API.Data.Enum;
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

        await Clients.Users(groupPairs).Client_GroupSendInfo(new GroupInfoDto(group.ToGroupData(), newOwnerPair.GroupUser.ToUserData(), group.ToEnum())).ConfigureAwait(false);
    }

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

        foreach (var pair in notPinned)
        {
            await Clients.Users(groupPairs.Where(p => p.IsPinned || p.IsModerator).Select(g => g.GroupUserUID))
                .Client_GroupPairLeft(new GroupPairDto(dto.Group, pair.GroupUser.ToUserData())).ConfigureAwait(false);

            var pairIdent = await GetUserIdent(pair.GroupUserUID).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pairIdent)) continue;

            foreach (var groupUserPair in groupPairs.Where(p => !string.Equals(p.GroupUserUID, pair.GroupUserUID, StringComparison.Ordinal)))
            {
                await UserGroupLeave(groupUserPair, pairIdent, pair.GroupUserUID).ConfigureAwait(false);
            }
        }

        await _dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<GroupJoinDto> GroupCreate()
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

        await Clients.User(UserUID).Client_GroupSendFullInfo(new GroupFullInfoDto(newGroup.ToGroupData(), self.ToUserData(),
            newGroup.ToEnum(), initialPrefPermissions.ToEnum(), initialPair.ToEnum(), new(StringComparer.Ordinal)))
            .ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(gid));

        return new GroupJoinDto(newGroup.ToGroupData(), passwd, initialPrefPermissions.ToEnum());
    }

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

    [Authorize(Policy = "Identified")]
    public async Task<GroupJoinInfoDto> GroupJoin(GroupPasswordDto dto)
    {
        var aliasOrGid = dto.Group.GID.Trim();

        _logger.LogCallInfo(MareHubLogger.Args(dto));

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
            return new GroupJoinInfoDto(null, null, GroupPermissions.NoneSet, false);

        return new GroupJoinInfoDto(group.ToGroupData(), group.Owner.ToUserData(), group.ToEnum(), true);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupJoinFinalize(GroupJoinDto dto)
    {
        var aliasOrGid = dto.Group.GID.Trim();

        _logger.LogCallInfo(MareHubLogger.Args(dto));

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

        // get all pairs before we join
        var allUserPairs = (await GetAllPairInfo(UserUID).ConfigureAwait(false));

        if (oneTimeInvite != null)
        {
            _logger.LogCallInfo(MareHubLogger.Args(aliasOrGid, "TempInvite", oneTimeInvite.Invite));
            _dbContext.Remove(oneTimeInvite);
        }

        GroupPair newPair = new()
        {
            GroupGID = group.GID,
            GroupUserUID = UserUID,
        };

        var preferredPermissions = await _dbContext.GroupPairPreferredPermissions.SingleOrDefaultAsync(u => u.UserUID == UserUID && u.GroupGID == group.GID).ConfigureAwait(false);
        if (preferredPermissions == null)
        {
            GroupPairPreferredPermission newPerms = new()
            {
                GroupGID = group.GID,
                UserUID = UserUID,
                DisableSounds = dto.GroupUserPreferredPermissions.IsDisableSounds(),
                DisableVFX = dto.GroupUserPreferredPermissions.IsDisableVFX(),
                DisableAnimations = dto.GroupUserPreferredPermissions.IsDisableAnimations(),
                IsPaused = false
            };

            _dbContext.Add(newPerms);
            preferredPermissions = newPerms;
        }
        else
        {
            preferredPermissions.DisableSounds = dto.GroupUserPreferredPermissions.IsDisableSounds();
            preferredPermissions.DisableVFX = dto.GroupUserPreferredPermissions.IsDisableVFX();
            preferredPermissions.DisableAnimations = dto.GroupUserPreferredPermissions.IsDisableAnimations();
            preferredPermissions.IsPaused = false;
            _dbContext.Update(preferredPermissions);
        }

        await _dbContext.GroupPairs.AddAsync(newPair).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(aliasOrGid, "Success"));

        var groupInfos = await _dbContext.GroupPairs.Where(u => u.GroupGID == group.GID && (u.IsPinned || u.IsModerator)).ToListAsync().ConfigureAwait(false);
        await Clients.User(UserUID).Client_GroupSendFullInfo(new GroupFullInfoDto(group.ToGroupData(), group.Owner.ToUserData(),
            group.ToEnum(), preferredPermissions.ToEnum(), newPair.ToEnum(),
            groupInfos.ToDictionary(u => u.GroupUserUID, u => u.ToEnum(), StringComparer.Ordinal))).ConfigureAwait(false);

        var self = _dbContext.Users.Single(u => u.UID == UserUID);

        var groupPairs = await _dbContext.GroupPairs.Include(p => p.GroupUser)
            .Where(p => p.GroupGID == group.GID && p.GroupUserUID != UserUID).ToListAsync().ConfigureAwait(false);

        var userPairsAfterJoin = await GetAllPairInfo(UserUID).ConfigureAwait(false);

        foreach (var pair in groupPairs)
        {
            var perms = userPairsAfterJoin.TryGetValue(pair.GroupUserUID, out var userinfo);
            // check if we have had prior permissions to that pair, if not add them
            var ownPermissionsToOther = userinfo?.OwnPermissions ?? null;
            if (ownPermissionsToOther == null)
            {
                var existingPermissionsOnDb = await _dbContext.Permissions.SingleOrDefaultAsync(p => p.UserUID == UserUID && p.OtherUserUID == pair.GroupUserUID).ConfigureAwait(false);

                if (existingPermissionsOnDb == null)
                {
                    ownPermissionsToOther = new()
                    {
                        UserUID = UserUID,
                        OtherUserUID = pair.GroupUserUID,
                        DisableAnimations = preferredPermissions.DisableAnimations,
                        DisableSounds = preferredPermissions.DisableSounds,
                        DisableVFX = preferredPermissions.DisableVFX,
                        IsPaused = false,
                        Sticky = false
                    };

                    await _dbContext.Permissions.AddAsync(ownPermissionsToOther).ConfigureAwait(false);
                }
                else
                {
                    existingPermissionsOnDb.DisableAnimations = preferredPermissions.DisableAnimations;
                    existingPermissionsOnDb.DisableSounds = preferredPermissions.DisableSounds;
                    existingPermissionsOnDb.DisableVFX = preferredPermissions.DisableVFX;
                    existingPermissionsOnDb.IsPaused = false;
                    existingPermissionsOnDb.Sticky = false;

                    _dbContext.Update(existingPermissionsOnDb);

                    ownPermissionsToOther = existingPermissionsOnDb;
                }

            }
            else if (!ownPermissionsToOther.Sticky)
            {
                ownPermissionsToOther = await _dbContext.Permissions.SingleAsync(u => u.UserUID == UserUID && u.OtherUserUID == pair.GroupUserUID).ConfigureAwait(false);

                // update the existing permission only if it was not set to sticky
                ownPermissionsToOther.DisableAnimations = preferredPermissions.DisableAnimations;
                ownPermissionsToOther.DisableVFX = preferredPermissions.DisableVFX;
                ownPermissionsToOther.DisableSounds = preferredPermissions.DisableSounds;
                ownPermissionsToOther.IsPaused = false;

                _dbContext.Update(ownPermissionsToOther);
            }

            // get others permissionset to self and eventually update it
            var otherPermissionToSelf = userinfo?.OtherPermissions ?? null;
            if (otherPermissionToSelf == null)
            {
                var existingPermissionsOnDb = await _dbContext.Permissions.SingleOrDefaultAsync(p => p.UserUID == pair.GroupUserUID && p.OtherUserUID == UserUID).ConfigureAwait(false);

                if (existingPermissionsOnDb == null)
                {
                    var otherPreferred = await _dbContext.GroupPairPreferredPermissions.SingleAsync(u => u.GroupGID == group.GID && u.UserUID == pair.GroupUserUID).ConfigureAwait(false);
                    existingPermissionsOnDb = new()
                    {
                        UserUID = pair.GroupUserUID,
                        OtherUserUID = UserUID,
                        DisableAnimations = otherPreferred.DisableAnimations,
                        DisableSounds = otherPreferred.DisableSounds,
                        DisableVFX = otherPreferred.DisableVFX,
                        IsPaused = otherPreferred.IsPaused,
                        Sticky = false
                    };

                    await _dbContext.AddAsync(existingPermissionsOnDb).ConfigureAwait(false);
                }

                otherPermissionToSelf = existingPermissionsOnDb;
            }

            await Clients.User(UserUID).Client_GroupPairJoined(new GroupPairFullInfoDto(group.ToGroupData(),
                pair.ToUserData(), ownPermissionsToOther.ToUserPermissions(setSticky: ownPermissionsToOther.Sticky),
                otherPermissionToSelf.ToUserPermissions(setSticky: false))).ConfigureAwait(false);
            await Clients.User(pair.GroupUserUID).Client_GroupPairJoined(new GroupPairFullInfoDto(group.ToGroupData(),
                self.ToUserData(), otherPermissionToSelf.ToUserPermissions(setSticky: false),
                ownPermissionsToOther.ToUserPermissions(setSticky: false))).ConfigureAwait(false);

            // if not paired prior and neither has the permissions set to paused, send online
            if ((!allUserPairs.ContainsKey(pair.GroupUserUID) || (allUserPairs.TryGetValue(pair.GroupUserUID, out var info) && !info.IsSynced))
                && !otherPermissionToSelf.IsPaused && !ownPermissionsToOther.IsPaused)
            {
                var groupUserIdent = await GetUserIdent(pair.GroupUserUID).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(groupUserIdent))
                {
                    await Clients.User(UserUID).Client_UserSendOnline(new(pair.ToUserData(), groupUserIdent)).ConfigureAwait(false);
                    await Clients.User(pair.GroupUserUID).Client_UserSendOnline(new(self.ToUserData(), UserCharaIdent)).ConfigureAwait(false);
                }
            }
        }

        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

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

        var groupPairs = _dbContext.GroupPairs.Where(p => p.GroupGID == group.GID).AsNoTracking().ToList();
        await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).Client_GroupPairLeft(dto).ConfigureAwait(false);

        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        var userIdent = await GetUserIdent(dto.User.UID).ConfigureAwait(false);
        if (userIdent == null)
        {
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            return;
        }

        await Clients.User(dto.User.UID).Client_GroupDelete(new GroupDto(dto.Group)).ConfigureAwait(false);

        foreach (var groupUserPair in groupPairs)
        {
            await UserGroupLeave(groupUserPair, userIdent, dto.User.UID).ConfigureAwait(false);
        }

    }

    [Authorize(Policy = "Identified")]
    public async Task GroupSetUserInfo(GroupPairUserInfoDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        var (userExists, userPair) = await TryValidateUserInGroup(dto.Group.GID, dto.User.UID).ConfigureAwait(false);
        if (!userExists) return;

        var (userIsOwner, _) = await TryValidateOwner(dto.Group.GID).ConfigureAwait(false);
        var (userIsModerator, _) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);

        if (dto.GroupUserInfo.HasFlag(GroupPairUserInfo.IsPinned) && userIsModerator && !userPair.IsPinned)
        {
            userPair.IsPinned = true;
        }
        else if (userIsModerator && userPair.IsPinned)
        {
            userPair.IsPinned = false;
        }

        if (dto.GroupUserInfo.HasFlag(GroupPairUserInfo.IsModerator) && userIsOwner && !userPair.IsModerator)
        {
            userPair.IsModerator = true;
        }
        else if (userIsOwner && userPair.IsModerator)
        {
            userPair.IsModerator = false;
        }

        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupPairs = await _dbContext.GroupPairs.AsNoTracking().Where(p => p.GroupGID == dto.Group.GID).Select(p => p.GroupUserUID).ToListAsync().ConfigureAwait(false);
        await Clients.Users(groupPairs).Client_GroupPairChangeUserInfo(new GroupPairUserInfoDto(dto.Group, dto.User, userPair.ToEnum())).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<GroupFullInfoDto>> GroupsGetAll()
    {
        _logger.LogCallInfo();

        var groups = await _dbContext.GroupPairs.Include(g => g.Group).Include(g => g.Group.Owner).Where(g => g.GroupUserUID == UserUID).AsNoTracking().ToListAsync().ConfigureAwait(false);
        var preferredPermissions = (await _dbContext.GroupPairPreferredPermissions.Where(u => u.UserUID == UserUID).ToListAsync().ConfigureAwait(false))
            .Where(u => groups.Exists(k => string.Equals(k.GroupGID, u.GroupGID, StringComparison.Ordinal)))
            .ToDictionary(u => groups.First(f => string.Equals(f.GroupGID, u.GroupGID, StringComparison.Ordinal)), u => u);
        var groupInfos = await _dbContext.GroupPairs.Where(u => groups.Select(g => g.GroupGID).Contains(u.GroupGID) && (u.IsPinned || u.IsModerator))
            .ToListAsync().ConfigureAwait(false);

        return preferredPermissions.Select(g => new GroupFullInfoDto(g.Key.Group.ToGroupData(), g.Key.Group.Owner.ToUserData(),
                g.Key.Group.ToEnum(), g.Value.ToEnum(), g.Key.ToEnum(),
                groupInfos.Where(i => string.Equals(i.GroupGID, g.Key.GroupGID, StringComparison.Ordinal))
                .ToDictionary(i => i.GroupUserUID, i => i.ToEnum(), StringComparer.Ordinal))).ToList();
    }

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