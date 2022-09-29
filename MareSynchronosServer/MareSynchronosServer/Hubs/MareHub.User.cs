using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using MareSynchronosShared.Protos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Hubs
{
    public partial class MareHub
    {
        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendUserDeleteAccount)]
        public async Task DeleteAccount()
        {
            _logger.LogInformation("User {AuthenticatedUserId} deleted their account", AuthenticatedUserId);

            string userid = AuthenticatedUserId;
            var userEntry = await _dbContext.Users.SingleAsync(u => u.UID == userid).ConfigureAwait(false);
            var charaIdent = await _clientIdentService.GetCharacterIdentForUid(userid).ConfigureAwait(false);
            var ownPairData = await _dbContext.ClientPairs.Where(u => u.User.UID == userid).ToListAsync().ConfigureAwait(false);
            var auth = await _dbContext.Auth.SingleAsync(u => u.UserUID == userid).ConfigureAwait(false);
            var lodestone = await _dbContext.LodeStoneAuth.SingleOrDefaultAsync(a => a.User.UID == userid).ConfigureAwait(false);

            if (lodestone != null)
            {
                _dbContext.Remove(lodestone);
            }

            while (_dbContext.Files.Any(f => f.Uploader == userEntry))
            {
                await Task.Delay(1000).ConfigureAwait(false);
            }

            await _authServiceClient.RemoveAuthAsync(new RemoveAuthRequest() { Uid = userid }).ConfigureAwait(false);

            _dbContext.RemoveRange(ownPairData);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            var otherPairData = await _dbContext.ClientPairs.Include(u => u.User)
                .Where(u => u.OtherUser.UID == userid).ToListAsync().ConfigureAwait(false);
            foreach (var pair in otherPairData)
            {
                await Clients.User(pair.User.UID)
                    .SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                    {
                        OtherUID = userid,
                        IsRemoved = true
                    }, charaIdent).ConfigureAwait(false);
            }

            _mareMetrics.IncCounter(MetricsAPI.CounterUsersRegisteredDeleted, 1);

            _dbContext.RemoveRange(otherPairData);
            _dbContext.Remove(userEntry);
            _dbContext.Remove(auth);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeUserGetOnlineCharacters)]
        public async Task<List<string>> GetOnlineCharacters()
        {
            _logger.LogInformation("User {AuthenticatedUserId} requested online characters", AuthenticatedUserId);
            var ownIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);

            var usersToSendOnlineTo = await SendDataToAllPairedUsers(Api.OnUserAddOnlinePairedPlayer, ownIdent).ConfigureAwait(false);
            return usersToSendOnlineTo.Select(async e => await _clientIdentService.GetCharacterIdentForUid(e)).Select(t => t.Result).Distinct().ToList();
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeUserGetPairedClients)]
        public async Task<List<ClientPairDto>> GetPairedClients()
        {
            string userid = AuthenticatedUserId;
            var query =
                from userToOther in _dbContext.ClientPairs
                join otherToUser in _dbContext.ClientPairs
                    on new
                    {
                        user = userToOther.UserUID,
                        other = userToOther.OtherUserUID

                    } equals new
                    {
                        user = otherToUser.OtherUserUID,
                        other = otherToUser.UserUID
                    } into leftJoin
                from otherEntry in leftJoin.DefaultIfEmpty()
                where
                    userToOther.UserUID == userid
                select new
                {
                    userToOther.OtherUser.Alias,
                    userToOther.IsPaused,
                    OtherIsPaused = otherEntry != null && otherEntry.IsPaused,
                    userToOther.OtherUserUID,
                    IsSynced = otherEntry != null
                };

            return (await query.ToListAsync().ConfigureAwait(false)).Select(f => new ClientPairDto()
            {
                VanityUID = f.Alias,
                IsPaused = f.IsPaused,
                OtherUID = f.OtherUserUID,
                IsSynced = f.IsSynced,
                IsPausedFromOthers = f.OtherIsPaused
            }).ToList();
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeUserPushCharacterDataToVisibleClients)]
        public async Task PushCharacterDataToVisibleClients(CharacterCacheDto characterCache, List<string> visibleCharacterIds)
        {
            _logger.LogInformation("User {AuthenticatedUserId} pushing character data to {visibleCharacterIds} visible clients", AuthenticatedUserId, visibleCharacterIds.Count);

            var allPairedUsers = await GetAllNotPausedPairedOrGroupedUsers().ConfigureAwait(false);
            var allPairedUsersDict = allPairedUsers.ToDictionary(f => f, async f => await _clientIdentService.GetCharacterIdentForUid(f))
                .Where(f => visibleCharacterIds.Contains(f.Value.Result));

            var ownIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);

            await Clients.Users(allPairedUsersDict.Select(f => f.Key)).SendAsync(Api.OnUserReceiveCharacterData, characterCache, ownIdent).ConfigureAwait(false);

            _mareMetrics.IncCounter(MetricsAPI.CounterUserPushData);
            _mareMetrics.IncCounter(MetricsAPI.CounterUserPushDataTo, allPairedUsersDict.Count());
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendUserPairedClientAddition)]
        public async Task SendPairedClientAddition(string uid)
        {
            if (uid == AuthenticatedUserId || string.IsNullOrWhiteSpace(uid)) return;
            uid = uid.Trim();
            var user = await _dbContext.Users.SingleAsync(u => u.UID == AuthenticatedUserId).ConfigureAwait(false);

            var otherUser = await _dbContext.Users
                .SingleOrDefaultAsync(u => u.UID == uid || u.Alias == uid).ConfigureAwait(false);
            var existingEntry =
                await _dbContext.ClientPairs.AsNoTracking()
                    .FirstOrDefaultAsync(p =>
                        p.User.UID == AuthenticatedUserId && p.OtherUser.UID == otherUser.UID).ConfigureAwait(false);
            if (otherUser == null || existingEntry != null) return;
            _logger.LogInformation("User {AuthenticatedUserId} adding {uid} to whitelist", AuthenticatedUserId, uid);
            ClientPair wl = new ClientPair()
            {
                IsPaused = false,
                OtherUser = otherUser,
                User = user
            };
            await _dbContext.ClientPairs.AddAsync(wl).ConfigureAwait(false);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            var otherEntry = OppositeEntry(otherUser.UID);
            await Clients.User(user.UID)
                .SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                {
                    VanityUID = otherUser.Alias,
                    OtherUID = otherUser.UID,
                    IsPaused = false,
                    IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                    IsSynced = otherEntry != null
                }).ConfigureAwait(false);

            if (otherEntry == null) return;

            var otherIdent = await _clientIdentService.GetCharacterIdentForUid(otherUser.UID).ConfigureAwait(false);
            if (otherIdent == null) return;

            var userIdent = await _clientIdentService.GetCharacterIdentForUid(user.UID).ConfigureAwait(false);
            await Clients.User(otherUser.UID).SendAsync(Api.OnUserUpdateClientPairs,
                new ClientPairDto()
                {
                    VanityUID = user.Alias,
                    OtherUID = user.UID,
                    IsPaused = otherEntry.IsPaused,
                    IsPausedFromOthers = false,
                    IsSynced = true
                }).ConfigureAwait(false);

            var userGroupPairs = await GetGroupPairs().ConfigureAwait(false);
            if (userGroupPairs.Any(u => u.GroupUserUID == otherUser.UID)) return;
            var otherUserGroupPairs = await GetGroupPairs(otherUser.UID).ConfigureAwait(false);
            if (otherUserGroupPairs.Any(u => u.GroupUserUID == user.UID)) return;

            if (!otherEntry.IsPaused)
            {
                await Clients.User(user.UID)
                    .SendAsync(Api.OnUserAddOnlinePairedPlayer, otherIdent).ConfigureAwait(false);
                await Clients.User(otherUser.UID)
                    .SendAsync(Api.OnUserAddOnlinePairedPlayer, userIdent).ConfigureAwait(false);
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendUserPairedClientPauseChange)]
        public async Task SendPairedClientPauseChange(string otherUserUid, bool isPaused)
        {
            if (otherUserUid == AuthenticatedUserId) return;
            ClientPair pair = await _dbContext.ClientPairs.SingleOrDefaultAsync(w => w.UserUID == AuthenticatedUserId && w.OtherUserUID == otherUserUid).ConfigureAwait(false);
            if (pair == null) return;

            _logger.LogInformation("User {AuthenticatedUserId} changed pause status with {otherUserUid} to {isPaused}", AuthenticatedUserId, otherUserUid, isPaused);
            pair.IsPaused = isPaused;
            _dbContext.Update(pair);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            var otherEntry = OppositeEntry(otherUserUid);

            await Clients.User(AuthenticatedUserId)
                .SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = otherUserUid,
                    IsPaused = isPaused,
                    IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                    IsSynced = otherEntry != null
                }).ConfigureAwait(false);
            if (otherEntry != null)
            {
                await Clients.User(otherUserUid).SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = AuthenticatedUserId,
                    IsPaused = otherEntry.IsPaused,
                    IsPausedFromOthers = isPaused,
                    IsSynced = true
                }).ConfigureAwait(false);

                var selfCharaIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);
                var otherCharaIdent = await _clientIdentService.GetCharacterIdentForUid(pair.OtherUserUID).ConfigureAwait(false);

                if (selfCharaIdent == null || otherCharaIdent == null || otherEntry.IsPaused) return;

                if (isPaused)
                {
                    await Clients.User(AuthenticatedUserId).SendAsync(Api.OnUserRemoveOnlinePairedPlayer, otherCharaIdent).ConfigureAwait(false);
                    await Clients.User(otherUserUid).SendAsync(Api.OnUserRemoveOnlinePairedPlayer, selfCharaIdent).ConfigureAwait(false);
                }
                else
                {
                    await Clients.User(AuthenticatedUserId).SendAsync(Api.OnUserAddOnlinePairedPlayer, otherCharaIdent).ConfigureAwait(false);
                    await Clients.User(otherUserUid).SendAsync(Api.OnUserAddOnlinePairedPlayer, selfCharaIdent).ConfigureAwait(false);
                }
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendUserPairedClientRemoval)]
        public async Task SendPairedClientRemoval(string otherUserUid)
        {
            if (otherUserUid == AuthenticatedUserId) return;

            // check if client pair even exists
            ClientPair callerPair =
                await _dbContext.ClientPairs.SingleOrDefaultAsync(w => w.UserUID == AuthenticatedUserId && w.OtherUserUID == otherUserUid).ConfigureAwait(false);
            bool callerHadPaused = callerPair.IsPaused;
            if (callerPair == null) return;

            // delete from database, send update info to users pair list
            _logger.LogInformation("User {AuthenticatedUserId} removed {uid} from whitelist", AuthenticatedUserId, otherUserUid);
            _dbContext.ClientPairs.Remove(callerPair);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            await Clients.User(AuthenticatedUserId)
                .SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = otherUserUid,
                    IsRemoved = true
                }).ConfigureAwait(false);

            // check if opposite entry exists
            var oppositeClientPair = OppositeEntry(otherUserUid);
            if (oppositeClientPair == null) return;

            // check if other user is online, if no then there is no need to do anything further
            var otherIdent = await _clientIdentService.GetCharacterIdentForUid(otherUserUid).ConfigureAwait(false);
            if (otherIdent == null) return;

            // get own ident and 
            await Clients.User(otherUserUid).SendAsync(Api.OnUserUpdateClientPairs,
                new ClientPairDto()
                {
                    OtherUID = AuthenticatedUserId,
                    IsPausedFromOthers = false,
                    IsSynced = false
                }).ConfigureAwait(false);

            // if the other user had paused the user the state will be offline for either, do nothing
            bool otherHadPaused = oppositeClientPair.IsPaused;
            if (!callerHadPaused && otherHadPaused) return;

            var userGroupPairs = await GetGroupPairs().ConfigureAwait(false);
            bool isInCallerGroup = userGroupPairs.Any(u => u.GroupUserUID == otherUserUid);
            var otherUserGroupPairs = await GetGroupPairs(otherUserUid).ConfigureAwait(false);
            bool isInOtherUserGroup = userGroupPairs.Any(u => u.GroupUserUID == AuthenticatedUserId);

            // if neither user had paused each other and both are in unpaused groups, state will be online for both, do nothing
            if (!callerHadPaused && !otherHadPaused && isInCallerGroup && isInOtherUserGroup) return;

            // if neither user had paused each other and either is not in an unpaused group with each other, change state to offline
            if (!callerHadPaused && !otherHadPaused && (!isInCallerGroup || !isInOtherUserGroup))
            {
                var userIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);
                await Clients.User(AuthenticatedUserId).SendAsync(Api.OnUserRemoveOnlinePairedPlayer, otherIdent).ConfigureAwait(false);
                await Clients.User(otherUserUid).SendAsync(Api.OnUserRemoveOnlinePairedPlayer, userIdent).ConfigureAwait(false);
            }

            // if the caller had paused other but not the other has paused the caller and they are in an unpaused group together, change state to online
            if (callerHadPaused && !otherHadPaused && isInCallerGroup && isInOtherUserGroup)
            {
                var userIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);
                await Clients.User(AuthenticatedUserId).SendAsync(Api.OnUserAddOnlinePairedPlayer, otherIdent).ConfigureAwait(false);
                await Clients.User(otherUserUid).SendAsync(Api.OnUserAddOnlinePairedPlayer, userIdent).ConfigureAwait(false);
            }
        }

        private ClientPair OppositeEntry(string otherUID) =>
                                    _dbContext.ClientPairs.AsNoTracking().SingleOrDefault(w => w.User.UID == otherUID && w.OtherUser.UID == AuthenticatedUserId);
    }
}
