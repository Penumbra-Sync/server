using MareSynchronos.API;
using MareSynchronosShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    // TODO: remove all of this and migrate it to the discord bot eventually
    private List<string> OnlineAdmins => _dbContext.Users.Where(u => (u.IsModerator || u.IsAdmin)).Select(u => u.UID).ToList();

    [Authorize(Policy = "Admin")]
    public async Task AdminChangeModeratorStatus(string uid, bool isModerator)
    {
        var user = await _dbContext.Users.SingleOrDefaultAsync(u => u.UID == uid).ConfigureAwait(false);

        if (user == null) return;

        user.IsModerator = isModerator;
        _dbContext.Update(user);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        await Clients.Users(user.UID).Client_AdminForcedReconnect().ConfigureAwait(false);
    }

    [Authorize(Policy = "Moderator")]
    public async Task AdminDeleteBannedUser(BannedUserDto dto)
    {
        if (string.IsNullOrEmpty(dto.CharacterHash)) return;

        var existingUser =
            await _dbContext.BannedUsers.SingleOrDefaultAsync(b => b.CharacterIdentification == dto.CharacterHash).ConfigureAwait(false);
        if (existingUser == null)
        {
            return;
        }

        _dbContext.Remove(existingUser);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        await Clients.Users(OnlineAdmins).Client_AdminDeleteBannedUser(dto).ConfigureAwait(false);
    }

    [Authorize(Policy = "Admin")]
    public async Task AdminDeleteForbiddenFile(ForbiddenFileDto dto)
    {
        if (string.IsNullOrEmpty(dto.Hash)) return;

        var existingFile =
            await _dbContext.ForbiddenUploadEntries.SingleOrDefaultAsync(b => b.Hash == dto.Hash).ConfigureAwait(false);
        if (existingFile == null)
        {
            return;
        }

        _dbContext.Remove(existingFile);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        await Clients.Users(OnlineAdmins).Client_AdminDeleteForbiddenFile(dto).ConfigureAwait(false);
    }

    [Authorize(Policy = "Moderator")]
    public async Task<List<BannedUserDto>> AdminGetBannedUsers()
    {
        return await _dbContext.BannedUsers.AsNoTracking().Select(b => new BannedUserDto()
        {
            CharacterHash = b.CharacterIdentification,
            Reason = b.Reason
        }).ToListAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Moderator")]
    public async Task<List<ForbiddenFileDto>> AdminGetForbiddenFiles()
    {
        return await _dbContext.ForbiddenUploadEntries.AsNoTracking().Select(b => new ForbiddenFileDto()
        {
            Hash = b.Hash,
            ForbiddenBy = b.ForbiddenBy
        }).ToListAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Moderator")]
    public async Task<List<OnlineUserDto>> AdminGetOnlineUsers()
    {
        var users = await _dbContext.Users.AsNoTracking().ToListAsync().ConfigureAwait(false);
        return users.Select(user => new { user, GetIdentFromUidFromRedis(user.UID).Result }).Where(a => !string.IsNullOrEmpty(a.Result)).Select(b => new OnlineUserDto
        {
            CharacterNameHash = b.Result,
            UID = b.user.UID,
            IsModerator = b.user.IsModerator,
            IsAdmin = b.user.IsAdmin
        }).ToList();
    }

    [Authorize(Policy = "Moderator")]
    public async Task AdminUpdateOrAddBannedUser(BannedUserDto dto)
    {
        if (string.IsNullOrEmpty(dto.CharacterHash)) return;

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
        await Clients.Users(OnlineAdmins).Client_AdminUpdateOrAddBannedUser(dto).ConfigureAwait(false);
        //var bannedUser = _clientIdentService.GetUidForCharacterIdent(dto.CharacterHash);
        //if (!string.IsNullOrEmpty(bannedUser))
        //{
        //    await Clients.User(bannedUser).Client_AdminForcedReconnect().ConfigureAwait(false);
        //}
    }

    [Authorize(Policy = "Admin")]
    public async Task AdminUpdateOrAddForbiddenFile(ForbiddenFileDto dto)
    {
        if (string.IsNullOrEmpty(dto.Hash)) return;

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

        await Clients.Users(OnlineAdmins).Client_AdminUpdateOrAddForbiddenFile(dto).ConfigureAwait(false);
    }
}
