using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

            while (_dbContext.Files.Any(f => f.Uploader == userEntry))
            {
                await Task.Delay(1000);
            }

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

        [HubMethodName(Api.InvokeUserGetOnlineUsers)]
        public async Task<int> GetOnlineUsers()
        {
            return await _dbContext.Users.CountAsync(u => !string.IsNullOrEmpty(u.CharacterIdentification));
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
            var senderPairedUsers = await _dbContext.ClientPairs.AsNoTracking()
                .Include(w => w.User)
                .Include(w => w.OtherUser)
                .Where(w => w.User.UID == user.UID && !w.IsPaused
                    && visibleCharacterIds.Contains(w.OtherUser.CharacterIdentification))
                .Select(u => u.OtherUser).ToListAsync();

            foreach (var pairedUser in senderPairedUsers)
            {
                var isPaused = (await _dbContext.ClientPairs.AsNoTracking()
                    .FirstOrDefaultAsync(w =>
                    w.User.UID == pairedUser.UID && w.OtherUser.UID == user.UID))?.IsPaused ?? true;
                if (isPaused) continue;
                await Clients.User(pairedUser.UID).SendAsync(Api.OnUserReceiveCharacterData, characterCache,
                    user.CharacterIdentification);
            }

            MareMetrics.UserPushData.Inc();
            MareMetrics.UserPushDataTo.Inc(visibleCharacterIds.Count);
        }

        [HubMethodName(Api.InvokeUserRegister)]
        public async Task<string> Register()
        {
            using var sha256 = SHA256.Create();
            var user = new User();

            var hasValidUid = false;
            while (!hasValidUid)
            {
                var uid = GenerateRandomString(10);
                if (_dbContext.Users.Any(u => u.UID == uid)) continue;
                user.UID = uid;
                hasValidUid = true;
            }

            // make the first registered user on the service to admin
            if (!await _dbContext.Users.AnyAsync())
            {
                user.IsAdmin = true;
            }

            var computedHash = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(GenerateRandomString(64)))).Replace("-", "");
            var auth = new Auth()
            {
                HashedKey = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(computedHash)))
                    .Replace("-", ""),
                User = user
            };

            _dbContext.Users.Add(user);
            _dbContext.Auth.Add(auth);

            _logger.LogInformation("User registered: " + user.UID);

            MareMetrics.UsersRegistered.Inc();

            await _dbContext.SaveChangesAsync();
            return computedHash;
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
        public async Task SendPairedClientPauseChange(string uid, bool isPaused)
        {
            if (uid == AuthenticatedUserId) return;
            var user = await _dbContext.Users.AsNoTracking()
                .SingleAsync(u => u.UID == AuthenticatedUserId);
            var otherUser = await _dbContext.Users.AsNoTracking()
                .SingleOrDefaultAsync(u => u.UID == uid);
            if (otherUser == null) return;
            _logger.LogInformation("User " + AuthenticatedUserId + " changed pause status with " + uid + " to " + isPaused);
            ClientPair wl =
                await _dbContext.ClientPairs.SingleOrDefaultAsync(w => w.User == user && w.OtherUser == otherUser);
            wl.IsPaused = isPaused;
            _dbContext.Update(wl);
            await _dbContext.SaveChangesAsync();
            var otherEntry = OppositeEntry(uid);

            await Clients.User(user.UID)
                .SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = otherUser.UID,
                    IsPaused = isPaused,
                    IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                    IsSynced = otherEntry != null
                }, otherUser.CharacterIdentification);
            if (otherEntry != null)
            {
                await Clients.User(uid).SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = user.UID,
                    IsPaused = otherEntry.IsPaused,
                    IsPausedFromOthers = isPaused,
                    IsSynced = true
                }, user.CharacterIdentification);
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
