using System.Collections.Generic;
using System.Linq;
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
    public class AdminHub : BaseHub<AdminHub>
    {
        public AdminHub(MareDbContext context, ILogger<AdminHub> logger) : base(context, logger)
        {
        }

        private bool IsAdmin => DbContext.Users.Single(b => b.UID == AuthenticatedUserId).IsAdmin;

        private bool IsModerator => DbContext.Users.Single(b => b.UID == AuthenticatedUserId).IsModerator || IsAdmin;

        private List<string> OnlineAdmins => DbContext.Users.Where(u => !string.IsNullOrEmpty(u.CharacterIdentification) && (u.IsModerator || u.IsAdmin))
                                .Select(u => u.UID).ToList();
        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(AdminHubAPI.SendChangeModeratorStatus)]
        public async Task ChangeModeratorStatus(string uid, bool isModerator)
        {
            if (!IsAdmin) return;
            var user = await DbContext.Users.SingleOrDefaultAsync(u => u.UID == uid);

            if (user == null) return;

            user.IsModerator = isModerator;
            DbContext.Update(user);
            await DbContext.SaveChangesAsync();
            await Clients.Users(user.UID).SendAsync(AdminHubAPI.OnForcedReconnect);
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(AdminHubAPI.SendDeleteBannedUser)]
        public async Task DeleteBannedUser(BannedUserDto dto)
        {
            if (!IsModerator || string.IsNullOrEmpty(dto.CharacterHash)) return;

            var existingUser =
                await DbContext.BannedUsers.SingleOrDefaultAsync(b => b.CharacterIdentification == dto.CharacterHash);
            if (existingUser == null)
            {
                return;
            }

            DbContext.Remove(existingUser);
            await DbContext.SaveChangesAsync();
            await Clients.Users(OnlineAdmins).SendAsync(AdminHubAPI.OnDeleteBannedUser, dto);
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(AdminHubAPI.SendDeleteForbiddenFile)]
        public async Task DeleteForbiddenFile(ForbiddenFileDto dto)
        {
            if (!IsAdmin || string.IsNullOrEmpty(dto.Hash)) return;

            var existingFile =
                await DbContext.ForbiddenUploadEntries.SingleOrDefaultAsync(b => b.Hash == dto.Hash);
            if (existingFile == null)
            {
                return;
            }

            DbContext.Remove(existingFile);
            await DbContext.SaveChangesAsync();
            await Clients.Users(OnlineAdmins).SendAsync(AdminHubAPI.OnDeleteForbiddenFile, dto);
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(AdminHubAPI.InvokeGetBannedUsers)]
        public async Task<List<BannedUserDto>> GetBannedUsers()
        {
            if (!IsModerator) return null;

            return await DbContext.BannedUsers.AsNoTracking().Select(b => new BannedUserDto()
            {
                CharacterHash = b.CharacterIdentification,
                Reason = b.Reason
            }).ToListAsync();
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(AdminHubAPI.InvokeGetForbiddenFiles)]
        public async Task<List<ForbiddenFileDto>> GetForbiddenFiles()
        {
            if (!IsModerator) return null;

            return await DbContext.ForbiddenUploadEntries.AsNoTracking().Select(b => new ForbiddenFileDto()
            {
                Hash = b.Hash,
                ForbiddenBy = b.ForbiddenBy
            }).ToListAsync();
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(AdminHubAPI.InvokeGetOnlineUsers)]
        public async Task<List<OnlineUserDto>> GetOnlineUsers()
        {
            if (!IsModerator) return null;

            return await DbContext.Users.AsNoTracking().Where(b => !string.IsNullOrEmpty(b.CharacterIdentification)).Select(b => new OnlineUserDto
            {
                CharacterNameHash = b.CharacterIdentification,
                UID = b.UID,
                IsModerator = b.IsModerator,
                IsAdmin = b.IsAdmin
            }).ToListAsync();
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(AdminHubAPI.SendUpdateOrAddBannedUser)]
        public async Task UpdateOrAddBannedUser(BannedUserDto dto)
        {
            if (!IsModerator || string.IsNullOrEmpty(dto.CharacterHash)) return;

            var existingUser =
                await DbContext.BannedUsers.SingleOrDefaultAsync(b => b.CharacterIdentification == dto.CharacterHash);
            if (existingUser != null)
            {
                existingUser.Reason = dto.Reason;
                DbContext.Update(existingUser);
            }
            else
            {
                await DbContext.BannedUsers.AddAsync(new Banned
                {
                    CharacterIdentification = dto.CharacterHash,
                    Reason = dto.Reason
                });
            }

            await DbContext.SaveChangesAsync();
            await Clients.Users(OnlineAdmins).SendAsync(AdminHubAPI.OnUpdateOrAddBannedUser, dto);
            var bannedUser =
                await DbContext.Users.SingleOrDefaultAsync(u => u.CharacterIdentification == dto.CharacterHash);
            if (bannedUser != null)
            {
                await Clients.User(bannedUser.UID).SendAsync(AdminHubAPI.OnForcedReconnect);
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(AdminHubAPI.SendUpdateOrAddForbiddenFile)]
        public async Task UpdateOrAddForbiddenFile(ForbiddenFileDto dto)
        {
            if (!IsAdmin || string.IsNullOrEmpty(dto.Hash)) return;

            var existingForbiddenFile =
                await DbContext.ForbiddenUploadEntries.SingleOrDefaultAsync(b => b.Hash == dto.Hash);
            if (existingForbiddenFile != null)
            {
                existingForbiddenFile.ForbiddenBy = dto.ForbiddenBy;
                DbContext.Update(existingForbiddenFile);
            }
            else
            {
                await DbContext.ForbiddenUploadEntries.AddAsync(new ForbiddenUploadEntry
                {
                    Hash = dto.Hash,
                    ForbiddenBy = dto.ForbiddenBy
                });
            }

            await DbContext.SaveChangesAsync();

            await Clients.Users(OnlineAdmins).SendAsync(AdminHubAPI.OnUpdateOrAddForbiddenFile, dto);
        }
    }
}
