using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
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
    public class User : Hub
    {
        private readonly MareDbContext _dbContext;

        public User(MareDbContext dbContext)
        {
            _dbContext = dbContext;
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
                if (_dbContext.Users.Any(u => u.UID == uid)) continue;
                user.UID = uid;
                hasValidUid = true;
            }
            _dbContext.Users.Add(user);

            await _dbContext.SaveChangesAsync();
            return computedHash;
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public string GetUID()
        {
            return Context.User!.Claims.Single(c => c.Type == ClaimTypes.NameIdentifier).Value;
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task SendWhitelist(List<WhitelistDto> whiteListEntries)
        {
            var currentUserId = Context.User!.Claims.Single(c => c.Type == ClaimTypes.NameIdentifier).Value;
            var user = _dbContext.Users.Single(u => u.UID == currentUserId);
            var userWhitelists = _dbContext.Whitelists
                .Include(w => w.User)
                .Include(w => w.OtherUser)
                .Where(w => w.User.UID == currentUserId)
                .ToList();
            foreach (var whitelist in whiteListEntries)
            {
                var otherEntry = _dbContext.Whitelists.SingleOrDefault(w =>
                    w.User.UID == whitelist.OtherUID && w.OtherUser.UID == user.UID);

                var prevEntry = userWhitelists.SingleOrDefault(w => w.OtherUser.UID == whitelist.OtherUID);
                if (prevEntry != null)
                {
                    prevEntry.IsPaused = whitelist.IsPaused;
                }
                else
                {
                    var otherUser = _dbContext.Users.SingleOrDefault(u => u.UID == whitelist.OtherUID);
                    if (otherUser != null)
                    {
                        Whitelist wl = new Whitelist
                        {
                            User = user,
                            OtherUser = otherUser,
                            IsPaused = whitelist.IsPaused
                        };
                        otherEntry = wl;
                        await _dbContext.Whitelists.AddAsync(wl);
                    }
                }

                if (otherEntry != null)
                {
                    await Clients.User(whitelist.OtherUID).SendAsync("UpdateWhitelist", currentUserId, true,
                        whitelist.IsPaused);
                }

                await _dbContext.SaveChangesAsync();
            }

            foreach (var deletedEntry in userWhitelists.Where(u => whiteListEntries.All(e => e.OtherUID != u.OtherUser.UID)).ToList())
            {
                var otherEntry = _dbContext.Whitelists.SingleOrDefault(w =>
                    w.User.UID == deletedEntry.OtherUser.UID && w.OtherUser.UID == user.UID);
                if (otherEntry != null)
                {
                    await Clients.User(otherEntry.User.UID).SendAsync("UpdateWhitelist", currentUserId, false, false);
                }

                _dbContext.Whitelists.Remove(deletedEntry);
            }
            _dbContext.Whitelists.RemoveRange();
            await _dbContext.SaveChangesAsync();
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task<List<WhitelistDto>> GetWhitelist()
        {
            string userid = Context.User!.Claims.Single(c => c.Type == ClaimTypes.NameIdentifier).Value;
            return _dbContext.Whitelists.Include(u => u.OtherUser).Include(u => u.User).Where(w => w.User.UID == userid)
                .ToList()
                .Select(w =>
                {
                    var otherEntry = _dbContext.Whitelists.SingleOrDefault(a => a.User.UID == w.OtherUser.UID && a.OtherUser.UID == userid);
                    return new WhitelistDto
                    {
                        IsPaused = w.IsPaused,
                        OtherUID = w.OtherUser.UID,
                        IsSynced = otherEntry != null,
                        IsPausedFromOthers = otherEntry?.IsPaused ?? false
                    };
                }).ToList();
        }

        public static string GenerateRandomString(int length, string allowableChars = null)
        {
            if (string.IsNullOrEmpty(allowableChars))
                allowableChars = @"ABCDEFGHJKLMNPQRSTUVWXYZ0123456789";

            // Generate random data
            var rnd = new byte[length];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(rnd);

            // Generate the output string
            var allowable = allowableChars.ToCharArray();
            var l = allowable.Length;
            var chars = new char[length];
            for (var i = 0; i < length; i++)
                chars[i] = allowable[rnd[i] % l];

            return new string(chars);
        }
    }
}
