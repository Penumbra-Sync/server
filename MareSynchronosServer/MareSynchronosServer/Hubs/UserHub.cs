using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosServer.Authentication;
using MareSynchronosServer.Data;
using MareSynchronosServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Hubs
{
    public class UserHub : BaseHub<UserHub>
    {
        public UserHub(ILogger<UserHub> logger, MareDbContext dbContext) : base(dbContext, logger)
        {
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(UserHubAPI.SendDeleteAccount)]
        public async Task DeleteAccount()
        {
            Logger.LogInformation("User " + AuthenticatedUserId + " deleted their account");

            string userid = AuthenticatedUserId;
            var userEntry = await DbContext.Users.SingleAsync(u => u.UID == userid);
            var ownPairData = DbContext.ClientPairs.Where(u => u.User.UID == userid);
            DbContext.RemoveRange(ownPairData);
            await DbContext.SaveChangesAsync();
            var otherPairData = DbContext.ClientPairs.Include(u => u.User).Where(u => u.OtherUser.UID == userid);
            foreach (var pair in otherPairData)
            {
                await Clients.User(pair.User.UID)
                    .SendAsync(UserHubAPI.OnUpdateClientPairs, new ClientPairDto()
                    {
                        OtherUID = userid,
                        IsRemoved = true
                    }, userEntry.CharacterIdentification);
            }

            DbContext.RemoveRange(otherPairData);
            DbContext.Remove(userEntry);
            await DbContext.SaveChangesAsync();
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(UserHubAPI.InvokeGetOnlineCharacters)]
        public async Task<List<string>> GetOnlineCharacters()
        {
            Logger.LogInformation("User " + AuthenticatedUserId + " requested online characters");

            var ownUser = DbContext.Users.Single(u => u.UID == AuthenticatedUserId);
            var otherUsers = await DbContext.ClientPairs
                .Include(u => u.User)
                .Include(u => u.OtherUser)
                .Where(w => w.User == ownUser && !w.IsPaused)
                .Where(w => !string.IsNullOrEmpty(w.OtherUser.CharacterIdentification))
                .Select(e => e.OtherUser).ToListAsync();
            var otherEntries = await DbContext.ClientPairs.Include(u => u.User)
                .Where(u => otherUsers.Any(e => e == u.User) && u.OtherUser == ownUser && !u.IsPaused).ToListAsync();

            await Clients.Users(otherEntries.Select(e => e.User.UID)).SendAsync(UserHubAPI.OnAddOnlinePairedPlayer, ownUser.CharacterIdentification);
            await Clients.All.SendAsync(UserHubAPI.OnUsersOnline,
                await DbContext.Users.CountAsync(u => !string.IsNullOrEmpty(u.CharacterIdentification)));
            return otherEntries.Select(e => e.User.CharacterIdentification).Distinct().ToList();
        }

        [HubMethodName(UserHubAPI.InvokeGetOnlineUsers)]
        public async Task<int> GetOnlineUsers()
        {
            return await DbContext.Users.CountAsync(u => !string.IsNullOrEmpty(u.CharacterIdentification));
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(UserHubAPI.InvokeGetPairedClients)]
        public async Task<List<ClientPairDto>> GetPairedClients()
        {
            string userid = AuthenticatedUserId;
            var user = GetAuthenticatedUser();
            return DbContext.ClientPairs
                .Include(u => u.OtherUser)
                .Include(u => u.User)
                .Where(w => w.User.UID == userid)
                .ToList()
                .Select(w =>
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

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            var user = DbContext.Users.SingleOrDefault(u => u.UID == AuthenticatedUserId);
            if (user != null)
            {
                Logger.LogInformation("Disconnect from " + AuthenticatedUserId);

                var otherUsers = DbContext.ClientPairs
                    .Include(u => u.User)
                    .Include(u => u.OtherUser)
                    .Where(w => w.User == user && !w.IsPaused)
                    .Where(w => !string.IsNullOrEmpty(w.OtherUser.CharacterIdentification))
                    .Select(e => e.OtherUser).ToList();
                var otherEntries = DbContext.ClientPairs.Include(u => u.User)
                    .Where(u => otherUsers.Any(e => e == u.User) && u.OtherUser == user && !u.IsPaused).ToList();
                await Clients.Users(otherEntries.Select(e => e.User.UID)).SendAsync(UserHubAPI.OnRemoveOnlinePairedPlayer, user.CharacterIdentification);

                user.CharacterIdentification = null;
                await DbContext.SaveChangesAsync();

                await Clients.All.SendAsync("UsersOnline",
                    await DbContext.Users.CountAsync(u => !string.IsNullOrEmpty(u.CharacterIdentification)));
            }

            await base.OnDisconnectedAsync(exception);
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(UserHubAPI.InvokePushCharacterDataToVisibleClients)]
        public async Task PushCharacterDataToVisibleClients(CharacterCacheDto characterCache, List<string> visibleCharacterIds)
        {
            Logger.LogInformation("User " + AuthenticatedUserId + " pushing character data to " + visibleCharacterIds.Count + " visible clients");

            var uid = AuthenticatedUserId;
            var entriesHavingThisUser = DbContext.ClientPairs
                .Include(w => w.User)
                .Include(w => w.OtherUser)
                .Where(w => w.OtherUser.UID == uid && !w.IsPaused
                    && visibleCharacterIds.Contains(w.User.CharacterIdentification)).ToList();

            foreach (var pair in entriesHavingThisUser)
            {
                var ownEntry = DbContext.ClientPairs.SingleOrDefault(w =>
                    w.User.UID == uid && w.OtherUser.UID == pair.User.UID);
                if (ownEntry == null || ownEntry.IsPaused) continue;
                await Clients.User(pair.User.UID).SendAsync(UserHubAPI.OnReceiveCharacterData, characterCache,
                    pair.OtherUser.CharacterIdentification);
            }
        }

        [HubMethodName(UserHubAPI.InvokeRegister)]
        public async Task<string> Register()
        {
            using var sha256 = SHA256.Create();
            var computedHash = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(GenerateRandomString(64)))).Replace("-", "");
            var user = new Models.User
            {
                SecretKey = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(computedHash)))
                    .Replace("-", ""),
            };

            var hasValidUid = false;
            while (!hasValidUid)
            {
                var uid = GenerateRandomString(10);
                if (DbContext.Users.Any(u => u.UID == uid)) continue;
                user.UID = uid;
                hasValidUid = true;
            }

            // make the first registered user on the service to admin
            if (!await DbContext.Users.AnyAsync())
            {
                user.IsAdmin = true;
            }

            DbContext.Users.Add(user);

            Logger.LogInformation("User registered: " + user.UID);

            await DbContext.SaveChangesAsync();
            return computedHash;
        }


        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(UserHubAPI.SendPairedClientAddition)]
        public async Task SendPairedClientAddition(string uid)
        {
            if (uid == AuthenticatedUserId) return;
            uid = uid.Trim();
            var user = await DbContext.Users.SingleAsync(u => u.UID == AuthenticatedUserId);
            var otherUser = await DbContext.Users.SingleOrDefaultAsync(u => u.UID == uid);
            var existingEntry =
                await DbContext.ClientPairs.SingleOrDefaultAsync(p =>
                    p.User.UID == AuthenticatedUserId && p.OtherUser.UID == uid);
            if (otherUser == null || existingEntry != null) return;
            Logger.LogInformation("User " + AuthenticatedUserId + " adding " + uid + " to whitelist");
            ClientPair wl = new ClientPair()
            {
                IsPaused = false,
                OtherUser = otherUser,
                User = user
            };
            await DbContext.ClientPairs.AddAsync(wl);
            await DbContext.SaveChangesAsync();
            var otherEntry = OppositeEntry(uid);
            await Clients.User(user.UID)
                .SendAsync(UserHubAPI.OnUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = otherUser.UID,
                    IsPaused = false,
                    IsPausedFromOthers = false,
                    IsSynced = otherEntry != null
                }, string.Empty);
            if (otherEntry != null)
            {
                if (!string.IsNullOrEmpty(otherUser.CharacterIdentification))
                {
                    await Clients.User(user.UID)
                        .SendAsync(UserHubAPI.OnAddOnlinePairedPlayer, otherUser.CharacterIdentification);
                    await Clients.User(otherUser.UID)
                        .SendAsync(UserHubAPI.OnAddOnlinePairedPlayer, user.CharacterIdentification);
                }

                await Clients.User(uid).SendAsync(UserHubAPI.OnUpdateClientPairs,
                    new ClientPairDto()
                    {
                        OtherUID = user.UID,
                        IsPaused = otherEntry.IsPaused,
                        IsPausedFromOthers = false,
                        IsSynced = true
                    }, user.CharacterIdentification);
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(UserHubAPI.SendPairedClientPauseChange)]
        public async Task SendPairedClientPauseChange(string uid, bool isPaused)
        {
            if (uid == AuthenticatedUserId) return;
            var user = DbContext.Users.Single(u => u.UID == AuthenticatedUserId);
            var otherUser = await DbContext.Users.SingleOrDefaultAsync(u => u.UID == uid);
            if (otherUser == null) return;
            Logger.LogInformation("User " + AuthenticatedUserId + " changed pause status with " + uid + " to " + isPaused);
            ClientPair wl =
                await DbContext.ClientPairs.SingleOrDefaultAsync(w => w.User == user && w.OtherUser == otherUser);
            wl.IsPaused = isPaused;
            DbContext.Update(wl);
            await DbContext.SaveChangesAsync();
            var otherEntry = OppositeEntry(uid);

            await Clients.User(user.UID)
                .SendAsync(UserHubAPI.OnUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = otherUser.UID,
                    IsPaused = isPaused,
                    IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                    IsSynced = otherEntry != null
                }, otherUser.CharacterIdentification);
            if (otherEntry != null)
            {
                await Clients.User(uid).SendAsync(UserHubAPI.OnUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = user.UID,
                    IsPaused = otherEntry.IsPaused,
                    IsPausedFromOthers = isPaused,
                    IsSynced = true
                }, user.CharacterIdentification);
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(UserHubAPI.SendPairedClientRemoval)]
        public async Task SendPairedClientRemoval(string uid)
        {
            if (uid == AuthenticatedUserId) return;

            var sender = await DbContext.Users.SingleAsync(u => u.UID == AuthenticatedUserId);
            var otherUser = await DbContext.Users.SingleOrDefaultAsync(u => u.UID == uid);
            if (otherUser == null) return;
            Logger.LogInformation("User " + AuthenticatedUserId + " removed " + uid + " from whitelist");
            ClientPair wl =
                await DbContext.ClientPairs.SingleOrDefaultAsync(w => w.User == sender && w.OtherUser == otherUser);
            if (wl == null) return;
            DbContext.ClientPairs.Remove(wl);
            await DbContext.SaveChangesAsync();
            var otherEntry = OppositeEntry(uid);
            await Clients.User(sender.UID)
                .SendAsync(UserHubAPI.OnUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = otherUser.UID,
                    IsRemoved = true
                }, otherUser.CharacterIdentification);
            if (otherEntry != null)
            {
                if (!string.IsNullOrEmpty(otherUser.CharacterIdentification))
                {
                    await Clients.User(sender.UID)
                        .SendAsync(UserHubAPI.OnRemoveOnlinePairedPlayer, otherUser.CharacterIdentification);
                    await Clients.User(otherUser.UID)
                        .SendAsync(UserHubAPI.OnRemoveOnlinePairedPlayer, sender.CharacterIdentification);
                    await Clients.User(otherUser.UID).SendAsync(UserHubAPI.OnUpdateClientPairs, new ClientPairDto()
                    {
                        OtherUID = sender.UID,
                        IsPaused = otherEntry.IsPaused,
                        IsPausedFromOthers = false,
                        IsSynced = false
                    }, sender.CharacterIdentification);
                }
            }
        }

        private ClientPair OppositeEntry(string otherUID) =>
                                    DbContext.ClientPairs.SingleOrDefault(w => w.User.UID == otherUID && w.OtherUser.UID == AuthenticatedUserId);
    }
}
