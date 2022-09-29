using MareSynchronos.API;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Models;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace MareSynchronosServer.Hubs
{
    public partial class MareHub
    {
        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeGroupCreate)]
        public async Task<GroupCreatedDto> CreateGroup()
        {
            var existingGroupsByUser = _dbContext.Groups.Count(u => u.OwnerUID == AuthenticatedUserId);
            if (existingGroupsByUser >= 3)
            {
                throw new System.Exception("Max groups for user is 3");
            }

            var gid = StringUtils.GenerateRandomString(12);
            while (await _dbContext.Groups.AnyAsync(g => g.GID == "G-" + gid).ConfigureAwait(false))
            {
                gid = StringUtils.GenerateRandomString(12);
            }
            gid = "G-" + gid;

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
            var groups = await _dbContext.GroupPairs.Where(g => g.GroupUserUID == AuthenticatedUserId).ToListAsync().ConfigureAwait(false);

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
            var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
            var existingPair = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == AuthenticatedUserId).ConfigureAwait(false);
            if (group == null || existingPair == null) return new List<GroupPairDto>();

            var allPairs = await _dbContext.GroupPairs.Where(g => g.GroupGID == gid && g.GroupUserUID != AuthenticatedUserId).ToListAsync().ConfigureAwait(false);
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

            var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == gid).ToListAsync().ConfigureAwait(false);
            await Clients.Users(groupPairs.Select(g => g.GroupUserUID)).SendAsync(Api.OnGroupChange, new GroupDto()
            {
                GID = group.GID,
                IsDeleted = true,
            });

            foreach (var pair in groupPairs)
            {
                var ownUserPairs = await GetAllUserPairs(pair.GroupUserUID);
                var sendOfflineTo = GetGroupUsersNotInUserPairs(ownUserPairs, groupPairs);
                var selfIdent = await _clientIdentService.GetCharacterIdentForUid(pair.GroupUserUID).ConfigureAwait(false);
                await Clients.Users(sendOfflineTo).SendAsync(Api.OnUserRemoveOnlinePairedPlayer, selfIdent).ConfigureAwait(false);
            }

            _dbContext.RemoveRange(groupPairs);
            _dbContext.Remove(group);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendGroupJoin)]
        public async Task GroupJoin(string gid, string password)
        {
            var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
            var existingPair = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == AuthenticatedUserId).ConfigureAwait(false);
            var hashedPw = StringUtils.Sha256String(password);
            if (group == null || !group.InvitesEnabled || group.HashedPassword != hashedPw || existingPair != null) return;

            var existingUserCount = await _dbContext.GroupPairs.CountAsync(g => g.GroupGID == gid).ConfigureAwait(false);
            if (existingUserCount >= 100)
            {
                throw new System.Exception("Group is full");
            }

            GroupPair newPair = new()
            {
                GroupGID = group.GID,
                GroupUserUID = AuthenticatedUserId
            };

            await _dbContext.GroupPairs.AddAsync(newPair).ConfigureAwait(false);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);

            await Clients.User(AuthenticatedUserId).SendAsync(Api.OnGroupChange, new GroupDto()
            {
                GID = group.GID,
                OwnedBy = AuthenticatedUserId,
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

            var userPairs = await GetAllUserPairs(AuthenticatedUserId);
            var clientIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);
            await Clients.Users(GetGroupUsersNotInUserPairs(userPairs, groupPairs)).SendAsync(Api.OnUserAddOnlinePairedPlayer, clientIdent).ConfigureAwait(false);
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendGroupLeave)]
        public async Task GroupLeave(string gid)
        {
            var groupPair = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == AuthenticatedUserId).ConfigureAwait(false);
            if (groupPair == null) return;

            var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);

            _dbContext.GroupPairs.Remove(groupPair);

            var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == group.GID).ToListAsync().ConfigureAwait(false);

            if (groupPair.GroupUserUID == AuthenticatedUserId)
            {
                var newOwner = await _dbContext.GroupPairs.FirstOrDefaultAsync(g => g.GroupGID == gid).ConfigureAwait(false);
                if (newOwner == null)
                {
                    _dbContext.Remove(group);
                }
                else
                {
                    group.OwnerUID = newOwner.GroupUserUID;

                    await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).SendAsync(Api.OnGroupChange, new GroupDto()
                    {
                        GID = group.GID,
                        OwnedBy = newOwner.GroupUserUID,
                    });
                }
            }

            await _dbContext.SaveChangesAsync().ConfigureAwait(false);

            await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).SendAsync(Api.OnGroupUserChange, new GroupPairDto()
            {
                GroupGID = group.GID,
                IsRemoved = true,
                UserUID = AuthenticatedUserId,
            });

            var userPairs = await GetAllUserPairs(AuthenticatedUserId);
            var clientIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);
            await Clients.Users(GetGroupUsersNotInUserPairs(userPairs, groupPairs)).SendAsync(Api.OnUserRemoveOnlinePairedPlayer, clientIdent).ConfigureAwait(false);
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendGroupPause)]
        public async Task GroupChangePauseState(string gid, bool isPaused)
        {
            var groupPair = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == AuthenticatedUserId).ConfigureAwait(false);
            if (groupPair == null) return;

            groupPair.IsPaused = isPaused;
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);

            var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupGID == gid).ToListAsync();
            await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).SendAsync(Api.OnGroupUserChange, new GroupPairDto()
            {
                GroupGID = gid,
                IsPaused = isPaused,
                UserUID = AuthenticatedUserId,
            });

            var userPairs = await GetAllUserPairs(AuthenticatedUserId);
            var clientIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);
            await Clients.Users(GetGroupUsersNotInUserPairs(userPairs, groupPairs)).SendAsync(isPaused ? Api.OnUserRemoveOnlinePairedPlayer : Api.OnUserAddOnlinePairedPlayer, clientIdent).ConfigureAwait(false);
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

            var userPairs = await GetAllUserPairs(uid);
            var userIdent = await _clientIdentService.GetCharacterIdentForUid(uid).ConfigureAwait(false);
            if (userIdent == null) return;

            // send offline state to everyone
            await Clients.Users(GetGroupUsersNotInUserPairs(userPairs, groupPairs)).SendAsync(Api.OnUserRemoveOnlinePairedPlayer, userIdent).ConfigureAwait(false);

            await Clients.User(uid).SendAsync(Api.OnGroupChange, new GroupDto()
            {
                GID = gid,
                IsDeleted = true,
            });

            foreach (var item in groupPairs.Where(a => !a.IsPaused && a.GroupUserUID != uid && !userPairs.Any(p => p.OtherUserUID == a.GroupUserUID)))
            {
                var ident = await _clientIdentService.GetCharacterIdentForUid(item.GroupUserUID).ConfigureAwait(false);
                if (ident != null)
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
            var groupPair = await _dbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == gid && g.GroupUserUID == uid).ConfigureAwait(false);
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
            var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
            if (group == null || group.OwnerUID != AuthenticatedUserId) return false;

            if (password.Length < 10) return false;

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
}
