using System;
using System.Collections.Generic;
using System.Diagnostics;
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

namespace MareSynchronosServer.Hubs
{
    public class User : BaseHub
    {
        public User(MareDbContext dbContext) : base(dbContext)
        {
        }

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
            DbContext.Users.Add(user);

            await DbContext.SaveChangesAsync();
            return computedHash;
        }

        private ClientPair OppositeEntry(string otherUID) =>
            DbContext.ClientPairs.SingleOrDefault(w => w.User.UID == otherUID && w.OtherUser.UID == AuthenticatedUserId);

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public string GetUID()
        {
            return AuthenticatedUserId;
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task SendVisibilityList(List<string> currentVisibilityList)
        {
            Stopwatch st = Stopwatch.StartNew();
            var cid = DbContext.Users.Single(u => u.UID == AuthenticatedUserId).CharacterIdentification;
            var visibilities = DbContext.Visibilities.Where(v => v.CID == cid).ToList();
            foreach (var visibility in currentVisibilityList.Where(visibility => visibilities.All(v => v.OtherCID != visibility)))
            {
                await DbContext.Visibilities.AddAsync(new Visibility { CID = cid, OtherCID = visibility });
            }

            foreach (var visibility in visibilities.Where(v => currentVisibilityList.Contains(v.OtherCID)))
            {
                DbContext.Visibilities.Remove(visibility);
            }

            await DbContext.SaveChangesAsync();
            st.Stop();
            Debug.WriteLine("Visibility update took " + st.Elapsed);
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task GetCharacterData(Dictionary<string, int> visibleCharacterWithJobs)
        {
            var uid = AuthenticatedUserId;
            Dictionary<string, CharacterCacheDto> ret = new();
            var entriesHavingThisUser = DbContext.ClientPairs
                .Include(w => w.User)
                .Include(w => w.OtherUser)
                .Where(w => w.OtherUser.UID == uid && !w.IsPaused && visibleCharacterWithJobs.Keys.Contains(w.User.CharacterIdentification))
                .ToList();
            foreach (var pair in entriesHavingThisUser)
            {
                bool isNotPaused = await DbContext.ClientPairs.AnyAsync(w =>
                    !w.IsPaused && w.User.UID == uid && w.OtherUser.UID == pair.User.UID);
                if (!isNotPaused) continue;
                var dictEntry = visibleCharacterWithJobs[pair.User.CharacterIdentification];

                var cachedChar = await
                    DbContext.CharacterData.SingleOrDefaultAsync(c => c.UserId == pair.User.UID && c.JobId == dictEntry);
                if (cachedChar != null)
                {
                    await Clients.User(uid).SendAsync("ReceiveCharacterData", new CharacterCacheDto()
                    {
                        FileReplacements = cachedChar.EquipmentData,
                        Hash = cachedChar.Hash,
                        JobId = cachedChar.JobId,
                        GlamourerData = cachedChar.GlamourerData
                    },
                        pair.User.CharacterIdentification);
                }
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task PushCharacterData(CharacterCacheDto characterCache, List<string> visibleCharacterIds)
        {
            var uid = AuthenticatedUserId;
            var entriesHavingThisUser = DbContext.ClientPairs
                .Include(w => w.User)
                .Include(w => w.OtherUser)
                .Where(w => w.OtherUser.UID == uid && !w.IsPaused
                    && visibleCharacterIds.Contains(w.User.CharacterIdentification)).ToList();
            var existingCharacterData =
                await DbContext.CharacterData.SingleOrDefaultAsync(s =>
                    s.UserId == uid && s.JobId == characterCache.JobId);
            if (existingCharacterData != null && existingCharacterData.Hash != characterCache.Hash)
            {
                existingCharacterData.EquipmentData =
                    characterCache.FileReplacements;
                existingCharacterData.Hash = characterCache.Hash;
                existingCharacterData.GlamourerData = characterCache.GlamourerData;
                DbContext.CharacterData.Update(existingCharacterData);
            }
            else if (existingCharacterData == null)
            {
                CharacterData data = new CharacterData
                {
                    UserId = AuthenticatedUserId,
                    EquipmentData = characterCache.FileReplacements,
                    Hash = characterCache.Hash,
                    GlamourerData = characterCache.GlamourerData,
                    JobId = characterCache.JobId
                };
                await DbContext.CharacterData.AddAsync(data);
                await DbContext.SaveChangesAsync();
            }

            if ((existingCharacterData != null && existingCharacterData.Hash != characterCache.Hash) || existingCharacterData == null)
            {
                foreach (var pair in entriesHavingThisUser)
                {
                    var ownEntry = DbContext.ClientPairs.SingleOrDefault(w =>
                        w.User.UID == uid && w.OtherUser.UID == pair.User.UID);
                    if (ownEntry == null || ownEntry.IsPaused) continue;
                    await Clients.User(pair.User.UID).SendAsync("ReceiveCharacterData", characterCache,
                        pair.OtherUser.CharacterIdentification);
                }
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task<List<string>> SendCharacterNameHash(string characterNameHash)
        {
            var ownUser = DbContext.Users.Single(u => u.UID == AuthenticatedUserId);
            ownUser.CharacterIdentification = characterNameHash;
            await DbContext.SaveChangesAsync();
            var otherUsers = await DbContext.ClientPairs
                .Include(u => u.User)
                .Include(u => u.OtherUser)
                .Where(w => w.User == ownUser)
                .Where(w => !string.IsNullOrEmpty(w.OtherUser.CharacterIdentification))
                .Select(e => e.OtherUser).ToListAsync();
            var otherEntries = await DbContext.ClientPairs.Include(u => u.User)
                .Where(u => otherUsers.Any(e => e == u.User) && u.OtherUser == ownUser).ToListAsync();

            await Clients.Users(otherEntries.Select(e => e.User.UID)).SendAsync("AddOnlinePairedPlayer", characterNameHash);
            return otherEntries.Select(e => e.User.CharacterIdentification).ToList();
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task SendPairedClientAddition(string uid)
        {
            var user = await DbContext.Users.SingleAsync(u => u.UID == AuthenticatedUserId);
            var otherUser = await DbContext.Users.SingleOrDefaultAsync(u => u.UID == uid);
            if (otherUser == null) return;
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
                .SendAsync("UpdateClientPairs", new ClientPairDto()
                {
                    OtherUID = otherUser.UID,
                    IsPaused = false,
                    IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                    IsSynced = otherEntry != null
                }, otherUser.CharacterIdentification);
            if (otherEntry != null)
            {
                if (string.IsNullOrEmpty(otherUser.CharacterIdentification))
                {
                    await Clients.User(user.UID)
                        .SendAsync("AddOnlinePairedPlayer", otherUser.CharacterIdentification);
                    await Clients.User(otherUser.UID)
                        .SendAsync("AddOnlinePairedPlayer", user.CharacterIdentification);
                }

                await Clients.User(uid).SendAsync("UpdateClientPairs",
                    new ClientPairDto()
                    {
                        OtherUID = user.UID,
                        IsPaused = otherEntry.IsPaused,
                        IsPausedFromOthers = false,
                        IsSynced = true
                    }, user.CharacterIdentification);
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task SendPairedClientRemoval(string uid)
        {
            var user = await DbContext.Users.SingleAsync(u => u.UID == AuthenticatedUserId);
            var otherUser = await DbContext.Users.SingleOrDefaultAsync(u => u.UID == uid);
            if (otherUser == null) return;
            ClientPair wl =
                await DbContext.ClientPairs.SingleOrDefaultAsync(w => w.User == user && w.OtherUser == otherUser);
            DbContext.ClientPairs.Remove(wl);
            await DbContext.SaveChangesAsync();
            var otherEntry = OppositeEntry(uid);
            await Clients.User(user.UID)
                .SendAsync("UpdateClientPairs", new ClientPairDto()
                {
                    OtherUID = otherUser.UID,
                    IsPaused = false,
                    IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                    IsSynced = otherEntry != null
                }, otherUser.CharacterIdentification);
            if (otherEntry != null)
            {
                if (string.IsNullOrEmpty(otherUser.CharacterIdentification))
                {
                    await Clients.User(user.UID)
                        .SendAsync("RemoveOnlinePairedPlayer", otherUser.CharacterIdentification);
                    await Clients.User(otherUser.UID)
                        .SendAsync("RemoveOnlinePairedPlayer", user.CharacterIdentification);
                }
                await Clients.User(uid).SendAsync("UpdateClientPairs", new ClientPairDto()
                {
                    OtherUID = user.UID,
                    IsPaused = otherEntry.IsPaused,
                    IsPausedFromOthers = false,
                    IsSynced = false
                }, user.CharacterIdentification);
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task SendPairedClientPauseChange(string uid, bool isPaused)
        {
            var user = DbContext.Users.Single(u => u.UID == AuthenticatedUserId);
            var otherUser = await DbContext.Users.SingleOrDefaultAsync(u => u.UID == uid);
            if (otherUser == null) return;
            ClientPair wl =
                await DbContext.ClientPairs.SingleOrDefaultAsync(w => w.User == user && w.OtherUser == otherUser);
            wl.IsPaused = isPaused;
            DbContext.Update(wl);
            await DbContext.SaveChangesAsync();
            var otherEntry = OppositeEntry(uid);

            await Clients.User(user.UID)
                .SendAsync("UpdateClientPairs", new ClientPairDto()
                {
                    OtherUID = otherUser.UID,
                    IsPaused = isPaused,
                    IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                    IsSynced = otherEntry != null
                }, otherUser.CharacterIdentification);
            if (otherEntry != null)
            {
                await Clients.User(uid).SendAsync("UpdateClientPairs", new ClientPairDto()
                {
                    OtherUID = user.UID,
                    IsPaused = otherEntry.IsPaused,
                    IsPausedFromOthers = isPaused,
                    IsSynced = true
                }, user.CharacterIdentification);
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
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


        public override Task OnDisconnectedAsync(Exception exception)
        {
            var user = DbContext.Users.SingleOrDefault(u => u.UID == AuthenticatedUserId);
            if (user != null)
            {
                var otherUsers = DbContext.ClientPairs
                    .Include(u => u.User)
                    .Include(u => u.OtherUser)
                    .Where(w => w.User == user)
                    .Where(w => !string.IsNullOrEmpty(w.OtherUser.CharacterIdentification))
                    .Select(e => e.OtherUser).ToList();
                var otherEntries = DbContext.ClientPairs.Include(u => u.User)
                    .Where(u => otherUsers.Any(e => e == u.User) && u.OtherUser == user).ToList();
                _ = Clients.Users(otherEntries.Select(e => e.User.UID)).SendAsync("RemoveOnlinePairedPlayer", user.CharacterIdentification);

                var outdatedVisibilities = DbContext.Visibilities.Where(v => v.CID == user.CharacterIdentification);
                DbContext.RemoveRange(outdatedVisibilities);
                var outdatedCharacterData = DbContext.CharacterData.Where(v => v.UserId == user.UID);
                DbContext.RemoveRange(outdatedCharacterData);
                user.CharacterIdentification = null;
                DbContext.SaveChanges();
            }

            return base.OnDisconnectedAsync(exception);
        }
    }
}
