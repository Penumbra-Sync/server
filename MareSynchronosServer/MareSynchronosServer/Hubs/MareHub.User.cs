﻿using System.Collections.Generic;
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
            var charaIdent = _clientIdentService.GetCharacterIdentForUid(userid);
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

            _mareMetrics.DecGauge(MetricsAPI.GaugePairs, ownPairData.Count + otherPairData.Count);
            _mareMetrics.DecGauge(MetricsAPI.GaugePairsPaused, ownPairData.Count(c => c.IsPaused));
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

            var userPairs = await GetAllUserPairs(AuthenticatedUserId);
            var ownGroups = await _dbContext.GroupPairs.Where(g => g.GroupUserUID == AuthenticatedUserId).Select(g => g.GroupGID).ToListAsync().ConfigureAwait(false);
            var groupPairs = await _dbContext.GroupPairs.Where(p => p.GroupUserUID != AuthenticatedUserId && ownGroups.Contains(p.GroupGID)).DistinctBy(p => p.GroupUserUID).ToListAsync().ConfigureAwait(false);
            var groupPairsWithoutUserPairs = GetGroupUsersNotInUserPairs(userPairs, groupPairs);
            var usersToSendOnlineTo = userPairs.Where(u => !u.UserPausedOther && u.OtherPausedUser).Select(u => u.UserUID).Concat(groupPairsWithoutUserPairs);
            var ownIdent = _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId);

            await Clients.Users(usersToSendOnlineTo).SendAsync(Api.OnUserAddOnlinePairedPlayer, ownIdent).ConfigureAwait(false);
            return usersToSendOnlineTo.Select(e => _clientIdentService.GetCharacterIdentForUid(e)).Distinct().ToList();
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

            var user = await GetAuthenticatedUserUntrackedAsync().ConfigureAwait(false);

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
                    }
                where
                    userToOther.UserUID == user.UID
                    && !userToOther.IsPaused
                    && !otherToUser.IsPaused
                select otherToUser.UserUID;

            var otherEntries = await query.ToListAsync().ConfigureAwait(false);
            otherEntries =
                otherEntries.Where(c => visibleCharacterIds.Select(c => c.ToLowerInvariant()).Contains(_clientIdentService.GetCharacterIdentForUid(c)?.ToLowerInvariant() ?? "")).ToList();
            var ownIdent = _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId);

            await Clients.Users(otherEntries).SendAsync(Api.OnUserReceiveCharacterData, characterCache, ownIdent).ConfigureAwait(false);

            _mareMetrics.IncCounter(MetricsAPI.CounterUserPushData);
            _mareMetrics.IncCounter(MetricsAPI.CounterUserPushDataTo, otherEntries.Count);
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
                }, string.Empty).ConfigureAwait(false);
            if (otherEntry != null)
            {
                var userIdent = _clientIdentService.GetCharacterIdentForUid(user.UID);
                await Clients.User(otherUser.UID).SendAsync(Api.OnUserUpdateClientPairs,
                    new ClientPairDto()
                    {
                        VanityUID = user.Alias,
                        OtherUID = user.UID,
                        IsPaused = otherEntry.IsPaused,
                        IsPausedFromOthers = false,
                        IsSynced = true
                    }, userIdent).ConfigureAwait(false);

                var otherIdent = _clientIdentService.GetCharacterIdentForUid(otherUser.UID);
                if (!string.IsNullOrEmpty(otherIdent))
                {
                    await Clients.User(user.UID)
                        .SendAsync(Api.OnUserAddOnlinePairedPlayer, otherIdent).ConfigureAwait(false);
                    await Clients.User(otherUser.UID)
                        .SendAsync(Api.OnUserAddOnlinePairedPlayer, userIdent).ConfigureAwait(false);
                }
            }

            _mareMetrics.IncGauge(MetricsAPI.GaugePairs);
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
            var selfCharaIdent = _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId);
            var otherCharaIdent = _clientIdentService.GetCharacterIdentForUid(pair.OtherUserUID);
            var otherEntry = OppositeEntry(otherUserUid);

            await Clients.User(AuthenticatedUserId)
                .SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = otherUserUid,
                    IsPaused = isPaused,
                    IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                    IsSynced = otherEntry != null
                }, otherCharaIdent).ConfigureAwait(false);
            if (otherEntry != null)
            {
                await Clients.User(otherUserUid).SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = AuthenticatedUserId,
                    IsPaused = otherEntry.IsPaused,
                    IsPausedFromOthers = isPaused,
                    IsSynced = true
                }, selfCharaIdent).ConfigureAwait(false);
            }

            if (isPaused)
            {
                _mareMetrics.IncGauge(MetricsAPI.GaugePairsPaused);
            }
            else
            {
                _mareMetrics.DecGauge(MetricsAPI.GaugePairsPaused);
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendUserPairedClientRemoval)]
        public async Task SendPairedClientRemoval(string uid)
        {
            if (uid == AuthenticatedUserId) return;

            var sender = await _dbContext.Users.SingleAsync(u => u.UID == AuthenticatedUserId).ConfigureAwait(false);
            var otherUser = await _dbContext.Users.SingleOrDefaultAsync(u => u.UID == uid).ConfigureAwait(false);
            if (otherUser == null) return;
            _logger.LogInformation("User {AuthenticatedUserId} removed {uid} from whitelist", AuthenticatedUserId, uid);
            ClientPair wl =
                await _dbContext.ClientPairs.SingleOrDefaultAsync(w => w.User == sender && w.OtherUser == otherUser).ConfigureAwait(false);
            if (wl == null) return;
            _dbContext.ClientPairs.Remove(wl);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            var otherEntry = OppositeEntry(uid);
            var otherIdent = _clientIdentService.GetCharacterIdentForUid(otherUser.UID);
            await Clients.User(sender.UID)
                .SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = otherUser.UID,
                    IsRemoved = true
                }, otherIdent).ConfigureAwait(false);
            if (otherEntry != null)
            {
                if (!string.IsNullOrEmpty(otherIdent))
                {
                    var ownIdent = _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId);
                    await Clients.User(sender.UID)
                        .SendAsync(Api.OnUserRemoveOnlinePairedPlayer, otherIdent).ConfigureAwait(false);
                    await Clients.User(otherUser.UID)
                        .SendAsync(Api.OnUserRemoveOnlinePairedPlayer, ownIdent).ConfigureAwait(false);
                    await Clients.User(otherUser.UID).SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                    {
                        OtherUID = sender.UID,
                        IsPaused = otherEntry.IsPaused,
                        IsPausedFromOthers = false,
                        IsSynced = false
                    }, ownIdent).ConfigureAwait(false);
                }
            }

            _mareMetrics.DecGauge(MetricsAPI.GaugePairs);
        }

        private ClientPair OppositeEntry(string otherUID) =>
                                    _dbContext.ClientPairs.AsNoTracking().SingleOrDefault(w => w.User.UID == otherUID && w.OtherUser.UID == AuthenticatedUserId);
    }
}
