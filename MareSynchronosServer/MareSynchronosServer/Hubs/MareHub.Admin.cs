using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs
{
    public partial class MareHub
    {
        private bool IsAdmin => _dbContext.Users.Single(b => b.UID == AuthenticatedUserId).IsAdmin;

        private bool IsModerator => _dbContext.Users.Single(b => b.UID == AuthenticatedUserId).IsModerator || IsAdmin;

        private List<string> OnlineAdmins => _dbContext.Users.Where(u => (u.IsModerator || u.IsAdmin)).Select(u => u.UID).ToList();

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendAdminChangeModeratorStatus)]
        public async Task ChangeModeratorStatus(string uid, bool isModerator)
        {
            if (!IsAdmin) return;
            var user = await _dbContext.Users.SingleOrDefaultAsync(u => u.UID == uid).ConfigureAwait(false);

            if (user == null) return;

            user.IsModerator = isModerator;
            _dbContext.Update(user);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            await Clients.Users(user.UID).SendAsync(Api.OnAdminForcedReconnect).ConfigureAwait(false);
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendAdminDeleteBannedUser)]
        public async Task DeleteBannedUser(BannedUserDto dto)
        {
            if (!IsModerator || string.IsNullOrEmpty(dto.CharacterHash)) return;

            var existingUser =
                await _dbContext.BannedUsers.SingleOrDefaultAsync(b => b.CharacterIdentification == dto.CharacterHash).ConfigureAwait(false);
            if (existingUser == null)
            {
                return;
            }

            _dbContext.Remove(existingUser);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            await Clients.Users(OnlineAdmins).SendAsync(Api.OnAdminDeleteBannedUser, dto).ConfigureAwait(false);
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendAdminDeleteForbiddenFile)]
        public async Task DeleteForbiddenFile(ForbiddenFileDto dto)
        {
            if (!IsAdmin || string.IsNullOrEmpty(dto.Hash)) return;

            var existingFile =
                await _dbContext.ForbiddenUploadEntries.SingleOrDefaultAsync(b => b.Hash == dto.Hash).ConfigureAwait(false);
            if (existingFile == null)
            {
                return;
            }

            _dbContext.Remove(existingFile);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            await Clients.Users(OnlineAdmins).SendAsync(Api.OnAdminDeleteForbiddenFile, dto).ConfigureAwait(false);
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeAdminGetBannedUsers)]
        public async Task<List<BannedUserDto>> GetBannedUsers()
        {
            if (!IsModerator) return null;

            return await _dbContext.BannedUsers.AsNoTracking().Select(b => new BannedUserDto()
            {
                CharacterHash = b.CharacterIdentification,
                Reason = b.Reason
            }).ToListAsync().ConfigureAwait(false);
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeAdminGetForbiddenFiles)]
        public async Task<List<ForbiddenFileDto>> GetForbiddenFiles()
        {
            if (!IsModerator) return null;

            return await _dbContext.ForbiddenUploadEntries.AsNoTracking().Select(b => new ForbiddenFileDto()
            {
                Hash = b.Hash,
                ForbiddenBy = b.ForbiddenBy
            }).ToListAsync().ConfigureAwait(false);
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeAdminGetOnlineUsers)]
        public async Task<List<OnlineUserDto>> AdminGetOnlineUsers()
        {
            if (!IsModerator) return null;

            var users = await _dbContext.Users.AsNoTracking().ToListAsync().ConfigureAwait(false);
            return users.Where(c => !string.IsNullOrEmpty(_clientIdentService.GetCharacterIdentForUid(c.UID).Result)).Select(async b => new OnlineUserDto
            {
                CharacterNameHash = await _clientIdentService.GetCharacterIdentForUid(b.UID).ConfigureAwait(false),
                UID = b.UID,
                IsModerator = b.IsModerator,
                IsAdmin = b.IsAdmin
            }).Select(c => c.Result).ToList();
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendAdminUpdateOrAddBannedUser)]
        public async Task UpdateOrAddBannedUser(BannedUserDto dto)
        {
            if (!IsModerator || string.IsNullOrEmpty(dto.CharacterHash)) return;

            var existingUser =
                await _dbContext.BannedUsers.SingleOrDefaultAsync(b => b.CharacterIdentification == dto.CharacterHash).ConfigureAwait(false);
            if (existingUser != null)
            {
                existingUser.Reason = dto.Reason;
                _dbContext.Update(existingUser);
            }
            else
            {
                await _dbContext.BannedUsers.AddAsync(new Banned
                {
                    CharacterIdentification = dto.CharacterHash,
                    Reason = dto.Reason
                }).ConfigureAwait(false);
            }

            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            await Clients.Users(OnlineAdmins).SendAsync(Api.OnAdminUpdateOrAddBannedUser, dto).ConfigureAwait(false);
            var bannedUser = await _clientIdentService.GetUidForCharacterIdent(dto.CharacterHash).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(bannedUser))
            {
                await Clients.User(bannedUser).SendAsync(Api.OnAdminForcedReconnect).ConfigureAwait(false);
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendAdminUpdateOrAddForbiddenFile)]
        public async Task UpdateOrAddForbiddenFile(ForbiddenFileDto dto)
        {
            if (!IsAdmin || string.IsNullOrEmpty(dto.Hash)) return;

            var existingForbiddenFile =
                await _dbContext.ForbiddenUploadEntries.SingleOrDefaultAsync(b => b.Hash == dto.Hash).ConfigureAwait(false);
            if (existingForbiddenFile != null)
            {
                existingForbiddenFile.ForbiddenBy = dto.ForbiddenBy;
                _dbContext.Update(existingForbiddenFile);
            }
            else
            {
                await _dbContext.ForbiddenUploadEntries.AddAsync(new ForbiddenUploadEntry
                {
                    Hash = dto.Hash,
                    ForbiddenBy = dto.ForbiddenBy
                }).ConfigureAwait(false);
            }

            await _dbContext.SaveChangesAsync().ConfigureAwait(false);

            await Clients.Users(OnlineAdmins).SendAsync(Api.OnAdminUpdateOrAddForbiddenFile, dto).ConfigureAwait(false);
        }
    }
}
