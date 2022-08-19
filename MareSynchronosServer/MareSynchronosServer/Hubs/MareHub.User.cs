using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosServer.Authentication;
using MareSynchronosServer.Metrics;
using MareSynchronosServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Hubs
{
    public partial class MareHub
    {
        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendUserDeleteAccount)]
        public async Task DeleteAccount()
        {
            _logger.LogInformation("User " + AuthenticatedUserId + " deleted their account");


            string userid = AuthenticatedUserId;
            var userEntry = await _dbContext.Users.SingleAsync(u => u.UID == userid);
            var ownPairData = await _dbContext.ClientPairs.Where(u => u.User.UID == userid).ToListAsync();
            var auth = await _dbContext.Auth.SingleAsync(u => u.UserUID == userid);
            var lodestone = await _dbContext.LodeStoneAuth.SingleOrDefaultAsync(a => a.User.UID == userid);

            if (lodestone != null)
            {
                _dbContext.Remove(lodestone);
            }

            while (_dbContext.Files.Any(f => f.Uploader == userEntry))
            {
                await Task.Delay(1000);
            }

            SecretKeyAuthenticationHandler.RemoveAuthentication(userid);

            MareMetrics.Pairs.Dec(ownPairData.Count);
            MareMetrics.PairsPaused.Dec(ownPairData.Count(c => c.IsPaused));

            _dbContext.RemoveRange(ownPairData);
            await _dbContext.SaveChangesAsync();
            var otherPairData = await _dbContext.ClientPairs.Include(u => u.User)
                .Where(u => u.OtherUser.UID == userid).ToListAsync();
            foreach (var pair in otherPairData)
            {
                await Clients.User(pair.User.UID)
                    .SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                    {
                        OtherUID = userid,
                        IsRemoved = true
                    }, userEntry.CharacterIdentification);
            }

            MareMetrics.Pairs.Dec(otherPairData.Count());
            MareMetrics.PairsPaused.Dec(otherPairData.Count(c => c.IsPaused));
            MareMetrics.UsersRegistered.Dec();

            _dbContext.RemoveRange(otherPairData);
            _dbContext.Remove(userEntry);
            _dbContext.Remove(auth);
            await _dbContext.SaveChangesAsync();
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeUserGetOnlineCharacters)]
        public async Task<List<string>> GetOnlineCharacters()
        {
            _logger.LogInformation("User " + AuthenticatedUserId + " requested online characters");

            var ownUser = await GetAuthenticatedUserUntrackedAsync();

            var otherUsers = await _dbContext.ClientPairs.AsNoTracking()
            .Include(u => u.User)
            .Include(u => u.OtherUser)
            .Where(w => w.User.UID == ownUser.UID && !w.IsPaused)
            .Where(w => !string.IsNullOrEmpty(w.OtherUser.CharacterIdentification))
            .Select(e => e.OtherUser).ToListAsync();
            var otherEntries = await _dbContext.ClientPairs.AsNoTracking()
                .Include(u => u.User)
                .Where(u => otherUsers.Any(e => e == u.User) && u.OtherUser == ownUser && !u.IsPaused).ToListAsync();

            await Clients.Users(otherEntries.Select(e => e.User.UID)).SendAsync(Api.OnUserAddOnlinePairedPlayer, ownUser.CharacterIdentification);
            return otherEntries.Select(e => e.User.CharacterIdentification).Distinct().ToList();
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeUserGetPairedClients)]
        public async Task<List<ClientPairDto>> GetPairedClients()
        {
            string userid = AuthenticatedUserId;
            var pairs = await _dbContext.ClientPairs.AsNoTracking()
                .Include(u => u.OtherUser)
                .Include(u => u.User)
                .Where(w => w.User.UID == userid)
                .ToListAsync();
            return pairs.Select(w =>
            {
                var otherEntry = OppositeEntry(w.OtherUser.UID);
                return new ClientPairDto
                {
                    IsPaused = w.IsPaused,
                    OtherUID = w.OtherUser.UID,
                    IsSynced = otherEntry != null,
                    IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                };
            }).ToList();
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeUserPushCharacterDataToVisibleClients)]
        public async Task PushCharacterDataToVisibleClients(CharacterCacheDto characterCache, List<string> visibleCharacterIds)
        {
            _logger.LogInformation("User " + AuthenticatedUserId + " pushing character data to " + visibleCharacterIds.Count + " visible clients");

            var user = await GetAuthenticatedUserUntrackedAsync();

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
                    && visibleCharacterIds.Contains(userToOther.OtherUser.CharacterIdentification)
                select otherToUser.UserUID;

            var otherEntries = await query.ToListAsync();

            await Clients.Users(otherEntries).SendAsync(Api.OnUserReceiveCharacterData, characterCache, user.CharacterIdentification);

            MareMetrics.UserPushData.Inc();
            MareMetrics.UserPushDataTo.Inc(otherEntries.Count);
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendUserPairedClientAddition)]
        public async Task SendPairedClientAddition(string uid)
        {
            if (uid == AuthenticatedUserId) return;
            uid = uid.Trim();
            var user = await _dbContext.Users.SingleAsync(u => u.UID == AuthenticatedUserId);

            var otherUser = await _dbContext.Users
                .SingleOrDefaultAsync(u => u.UID == uid);
            var existingEntry =
                await _dbContext.ClientPairs.AsNoTracking()
                    .FirstOrDefaultAsync(p =>
                    p.User.UID == AuthenticatedUserId && p.OtherUser.UID == uid);
            if (otherUser == null || existingEntry != null) return;
            _logger.LogInformation("User " + AuthenticatedUserId + " adding " + uid + " to whitelist");
            ClientPair wl = new ClientPair()
            {
                IsPaused = false,
                OtherUser = otherUser,
                User = user
            };
            await _dbContext.ClientPairs.AddAsync(wl);
            await _dbContext.SaveChangesAsync();
            var otherEntry = OppositeEntry(uid);
            await Clients.User(user.UID)
                .SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = otherUser.UID,
                    IsPaused = false,
                    IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                    IsSynced = otherEntry != null
                }, string.Empty);
            if (otherEntry != null)
            {
                await Clients.User(uid).SendAsync(Api.OnUserUpdateClientPairs,
                    new ClientPairDto()
                    {
                        OtherUID = user.UID,
                        IsPaused = otherEntry.IsPaused,
                        IsPausedFromOthers = false,
                        IsSynced = true
                    }, user.CharacterIdentification);

                if (!string.IsNullOrEmpty(otherUser.CharacterIdentification))
                {
                    await Clients.User(user.UID)
                        .SendAsync(Api.OnUserAddOnlinePairedPlayer, otherUser.CharacterIdentification);
                    await Clients.User(otherUser.UID)
                        .SendAsync(Api.OnUserAddOnlinePairedPlayer, user.CharacterIdentification);
                }
            }

            MareMetrics.Pairs.Inc();
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendUserPairedClientPauseChange)]
        public async Task SendPairedClientPauseChange(string otherUserUid, bool isPaused)
        {
            if (otherUserUid == AuthenticatedUserId) return;
            ClientPair pair = await _dbContext.ClientPairs.SingleOrDefaultAsync(w => w.UserUID == AuthenticatedUserId && w.OtherUserUID == otherUserUid);
            if (pair == null) return;

            _logger.LogInformation("User " + AuthenticatedUserId + " changed pause status with " + otherUserUid + " to " + isPaused);
            pair.IsPaused = isPaused;
            _dbContext.Update(pair);
            await _dbContext.SaveChangesAsync();
            var selfCharaIdent = (await _dbContext.Users.SingleAsync(u => u.UID == AuthenticatedUserId)).CharacterIdentification;
            var otherCharaIdent = (await _dbContext.Users.SingleAsync(u => u.UID == otherUserUid)).CharacterIdentification;
            var otherEntry = OppositeEntry(otherUserUid);

            await Clients.User(AuthenticatedUserId)
                .SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = otherUserUid,
                    IsPaused = isPaused,
                    IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                    IsSynced = otherEntry != null
                }, otherCharaIdent);
            if (otherEntry != null)
            {
                await Clients.User(otherUserUid).SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = AuthenticatedUserId,
                    IsPaused = otherEntry.IsPaused,
                    IsPausedFromOthers = isPaused,
                    IsSynced = true
                }, selfCharaIdent);
            }

            if (isPaused)
            {
                MareMetrics.PairsPaused.Inc();
            }
            else
            {
                MareMetrics.PairsPaused.Dec();
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendUserPairedClientRemoval)]
        public async Task SendPairedClientRemoval(string uid)
        {
            if (uid == AuthenticatedUserId) return;

            var sender = await _dbContext.Users.SingleAsync(u => u.UID == AuthenticatedUserId);
            var otherUser = await _dbContext.Users.SingleOrDefaultAsync(u => u.UID == uid);
            if (otherUser == null) return;
            _logger.LogInformation("User " + AuthenticatedUserId + " removed " + uid + " from whitelist");
            ClientPair wl =
                await _dbContext.ClientPairs.SingleOrDefaultAsync(w => w.User == sender && w.OtherUser == otherUser);
            if (wl == null) return;
            _dbContext.ClientPairs.Remove(wl);
            await _dbContext.SaveChangesAsync();
            var otherEntry = OppositeEntry(uid);
            await Clients.User(sender.UID)
                .SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = otherUser.UID,
                    IsRemoved = true
                }, otherUser.CharacterIdentification);
            if (otherEntry != null)
            {
                if (!string.IsNullOrEmpty(otherUser.CharacterIdentification))
                {
                    await Clients.User(sender.UID)
                        .SendAsync(Api.OnUserRemoveOnlinePairedPlayer, otherUser.CharacterIdentification);
                    await Clients.User(otherUser.UID)
                        .SendAsync(Api.OnUserRemoveOnlinePairedPlayer, sender.CharacterIdentification);
                    await Clients.User(otherUser.UID).SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                    {
                        OtherUID = sender.UID,
                        IsPaused = otherEntry.IsPaused,
                        IsPausedFromOthers = false,
                        IsSynced = false
                    }, sender.CharacterIdentification);
                }
            }

            MareMetrics.Pairs.Dec();
        }

        private ClientPair OppositeEntry(string otherUID) =>
                                    _dbContext.ClientPairs.AsNoTracking().SingleOrDefault(w => w.User.UID == otherUID && w.OtherUser.UID == AuthenticatedUserId);
    }
}
