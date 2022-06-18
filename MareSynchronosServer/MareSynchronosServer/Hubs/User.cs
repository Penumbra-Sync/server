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

        private Whitelist OppositeEntry(string otherUID) =>
            DbContext.Whitelists.SingleOrDefault(w => w.User.UID == otherUID && w.OtherUser.UID == AuthenticatedUserId);

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
            var whitelistEntriesHavingThisUser = DbContext.Whitelists
                .Include(w => w.User)
                .Include(w => w.OtherUser)
                .Where(w => w.OtherUser.UID == uid && !w.IsPaused
                                                   && visibleCharacterWithJobs.Keys.Contains(w.User.CharacterIdentification))
                .ToList();
            foreach (var whiteListEntry in whitelistEntriesHavingThisUser)
            {
                bool isNotPaused = await DbContext.Whitelists.AnyAsync(w =>
                    !w.IsPaused && w.User.UID == uid && w.OtherUser.UID == whiteListEntry.User.UID);
                if (!isNotPaused) continue;
                var dictEntry = visibleCharacterWithJobs[whiteListEntry.User.CharacterIdentification];

                var cachedChar = await
                    DbContext.CharacterData.SingleOrDefaultAsync(c => c.UserId == whiteListEntry.User.UID && c.JobId == dictEntry);
                if (cachedChar != null)
                {
                    await Clients.User(uid).SendAsync("ReceiveCharacterData", cachedChar,
                        whiteListEntry.User.CharacterIdentification);
                }
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task PushCharacterData(CharacterCacheDto characterCache, List<string> visibleCharacterIds)
        {
            var uid = AuthenticatedUserId;
            var whitelistEntriesHavingThisUser = DbContext.Whitelists
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
            }

            await DbContext.SaveChangesAsync();

            foreach (var whitelistEntry in whitelistEntriesHavingThisUser)
            {
                var ownEntry = DbContext.Whitelists.SingleOrDefault(w =>
                    w.User.UID == uid && w.OtherUser.UID == whitelistEntry.User.UID);
                if (ownEntry == null || ownEntry.IsPaused) continue;
                await Clients.User(whitelistEntry.User.UID).SendAsync("ReceiveCharacterData", characterCache, whitelistEntry.OtherUser.CharacterIdentification);
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task SendCharacterNameHash(string characterNameHash)
        {
            DbContext.Users.Single(u => u.UID == AuthenticatedUserId).CharacterIdentification = characterNameHash;
            await DbContext.SaveChangesAsync();
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task SendWhitelistAddition(string uid)
        {
            var user = await DbContext.Users.SingleAsync(u => u.UID == AuthenticatedUserId);
            var otherUser = await DbContext.Users.SingleOrDefaultAsync(u => u.UID == uid);
            if (otherUser == null) return;
            Whitelist wl = new Whitelist()
            {
                IsPaused = false,
                OtherUser = otherUser,
                User = user
            };
            await DbContext.Whitelists.AddAsync(wl);
            await DbContext.SaveChangesAsync();
            var otherEntry = OppositeEntry(uid);
            await Clients.User(user.UID)
                .SendAsync("UpdateWhitelist", new WhitelistDto()
                {
                    OtherUID = otherUser.UID,
                    IsPaused = false,
                    IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                    IsSynced = otherEntry != null
                }, otherUser.CharacterIdentification);
            if (otherEntry != null)
            {
                await Clients.User(uid).SendAsync("UpdateWhitelist",
                    new WhitelistDto()
                    {
                        OtherUID = user.UID,
                        IsPaused = otherEntry.IsPaused,
                        IsPausedFromOthers = false,
                        IsSynced = true
                    }, user.CharacterIdentification);
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task SendWhitelistRemoval(string uid)
        {
            var user = await DbContext.Users.SingleAsync(u => u.UID == AuthenticatedUserId);
            var otherUser = await DbContext.Users.SingleOrDefaultAsync(u => u.UID == uid);
            if (otherUser == null) return;
            Whitelist wl =
                await DbContext.Whitelists.SingleOrDefaultAsync(w => w.User == user && w.OtherUser == otherUser);
            DbContext.Whitelists.Remove(wl);
            await DbContext.SaveChangesAsync();
            var otherEntry = OppositeEntry(uid);
            await Clients.User(user.UID)
                .SendAsync("UpdateWhitelist", new WhitelistDto()
                {
                    OtherUID = otherUser.UID,
                    IsPaused = false,
                    IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                    IsSynced = otherEntry != null
                }, otherUser.CharacterIdentification);
            if (otherEntry != null)
            {
                await Clients.User(uid).SendAsync("UpdateWhitelist", new WhitelistDto()
                {
                    OtherUID = user.UID,
                    IsPaused = otherEntry.IsPaused,
                    IsPausedFromOthers = false,
                    IsSynced = false
                }, user.CharacterIdentification);
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task SendWhitelistPauseChange(string uid, bool isPaused)
        {
            var user = DbContext.Users.Single(u => u.UID == AuthenticatedUserId);
            var otherUser = await DbContext.Users.SingleOrDefaultAsync(u => u.UID == uid);
            if (otherUser == null) return;
            Whitelist wl =
                await DbContext.Whitelists.SingleOrDefaultAsync(w => w.User == user && w.OtherUser == otherUser);
            wl.IsPaused = isPaused;
            DbContext.Update(wl);
            await DbContext.SaveChangesAsync();
            var otherEntry = OppositeEntry(uid);
            await Clients.User(user.UID)
                .SendAsync("UpdateWhitelist", new WhitelistDto()
                {
                    OtherUID = otherUser.UID,
                    IsPaused = isPaused,
                    IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                    IsSynced = otherEntry != null
                }, otherUser.CharacterIdentification);
            if (otherEntry != null)
            {
                await Clients.User(uid).SendAsync("UpdateWhitelist", new WhitelistDto()
                {
                    OtherUID = user.UID,
                    IsPaused = otherEntry.IsPaused,
                    IsPausedFromOthers = isPaused,
                    IsSynced = true
                }, user.CharacterIdentification);
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task<List<WhitelistDto>> GetWhitelist()
        {
            string userid = AuthenticatedUserId;
            var user = GetAuthenticatedUser();
            return DbContext.Whitelists
                .Include(u => u.OtherUser)
                .Include(u => u.User)
                .Where(w => w.User.UID == userid)
                .ToList()
                .Select(w =>
                {
                    var otherEntry = OppositeEntry(w.OtherUser.UID);
                    var otherUser = GetUserFromUID(w.OtherUser.UID);
                    var seesYou = false;
                    if (otherEntry != null)
                    {
                        seesYou = DbContext.Visibilities.Any(v =>
                            v.CID == otherUser.CharacterIdentification && v.OtherCID == user.CharacterIdentification);
                    }
                    return new WhitelistDto
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
