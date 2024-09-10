using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MareSynchronosShared.Utils;

public static class SharedDbFunctions
{
    public static async Task<(bool, string)> MigrateOrDeleteGroup(MareDbContext context, Group group, List<GroupPair> groupPairs, int maxGroupsByUser)
    {
        bool groupHasMigrated = false;
        string newOwner = string.Empty;
        foreach (var potentialNewOwner in groupPairs.OrderByDescending(p => p.IsModerator).ThenByDescending(p => p.IsPinned).ToList())
        {
            groupHasMigrated = await TryMigrateGroup(context, group, potentialNewOwner.GroupUserUID, maxGroupsByUser).ConfigureAwait(false);

            if (groupHasMigrated)
            {
                newOwner = potentialNewOwner.GroupUserUID;
                potentialNewOwner.IsPinned = true;
                potentialNewOwner.IsModerator = false;
                break;
            }
        }

        if (!groupHasMigrated)
        {
            context.GroupPairs.RemoveRange(groupPairs);
            context.Groups.Remove(group);
        }

        return (groupHasMigrated, newOwner);
    }

    public static async Task PurgeUser(ILogger _logger, User user, MareDbContext dbContext, int maxGroupsByUser)
    {
        _logger.LogInformation("Purging user: {uid}", user.UID);

        var secondaryUsers = await dbContext.Auth.Include(u => u.User)
            .Where(u => u.PrimaryUserUID == user.UID).Select(c => c.User).ToListAsync().ConfigureAwait(false);

        foreach (var secondaryUser in secondaryUsers)
        {
            await PurgeUser(_logger, secondaryUser, dbContext, maxGroupsByUser).ConfigureAwait(false);
        }

        var lodestone = dbContext.LodeStoneAuth.SingleOrDefault(a => a.User.UID == user.UID);

        var userProfileData = await dbContext.UserProfileData.SingleOrDefaultAsync(u => u.UserUID == user.UID).ConfigureAwait(false);

        if (lodestone != null)
        {
            dbContext.Remove(lodestone);
        }

        if (userProfileData != null)
        {
            dbContext.Remove(userProfileData);
        }

        var auth = dbContext.Auth.Single(a => a.UserUID == user.UID);

        var userFiles = dbContext.Files.Where(f => f.Uploaded && f.Uploader.UID == user.UID).ToList();
        dbContext.Files.RemoveRange(userFiles);

        var ownPairData = dbContext.ClientPairs.Where(u => u.User.UID == user.UID).ToList();
        dbContext.ClientPairs.RemoveRange(ownPairData);
        var otherPairData = dbContext.ClientPairs.Include(u => u.User)
            .Where(u => u.OtherUser.UID == user.UID).ToList();
        dbContext.ClientPairs.RemoveRange(otherPairData);

        var userJoinedGroups = await dbContext.GroupPairs.Include(g => g.Group).Where(u => u.GroupUserUID == user.UID).ToListAsync().ConfigureAwait(false);

        foreach (var userGroupPair in userJoinedGroups)
        {
            bool ownerHasLeft = string.Equals(userGroupPair.Group.OwnerUID, user.UID, StringComparison.Ordinal);

            if (ownerHasLeft)
            {
                var groupPairs = await dbContext.GroupPairs.Where(g => g.GroupGID == userGroupPair.GroupGID && g.GroupUserUID != user.UID).ToListAsync().ConfigureAwait(false);

                if (!groupPairs.Any())
                {
                    _logger.LogInformation("Group {gid} has no new owner, deleting", userGroupPair.GroupGID);
                    dbContext.Groups.Remove(userGroupPair.Group);
                }
                else
                {
                    _ = await MigrateOrDeleteGroup(dbContext, userGroupPair.Group, groupPairs, maxGroupsByUser).ConfigureAwait(false);
                }
            }

            dbContext.GroupPairs.Remove(userGroupPair);
        }

        var defaultPermissions = await dbContext.UserDefaultPreferredPermissions.Where(u => u.UserUID == user.UID).ToListAsync().ConfigureAwait(false);
        var groupPermissions = await dbContext.GroupPairPreferredPermissions.Where(u => u.UserUID == user.UID).ToListAsync().ConfigureAwait(false);
        var individualPermissions = await dbContext.Permissions.Where(u => u.UserUID == user.UID || u.OtherUserUID == user.UID).ToListAsync().ConfigureAwait(false);
        var bannedinGroups = await dbContext.GroupBans.Where(u => u.BannedUserUID == user.UID).ToListAsync().ConfigureAwait(false);
        var hasBannedInGroups = await dbContext.GroupBans.Where(u => u.BannedByUID == user.UID).ToListAsync().ConfigureAwait(false);

        dbContext.GroupPairPreferredPermissions.RemoveRange(groupPermissions);
        dbContext.UserDefaultPreferredPermissions.RemoveRange(defaultPermissions);
        dbContext.Permissions.RemoveRange(individualPermissions);
        dbContext.GroupBans.RemoveRange(bannedinGroups);
        dbContext.GroupBans.RemoveRange(hasBannedInGroups);

        _logger.LogInformation("User purged: {uid}", user.UID);

        dbContext.Auth.Remove(auth);
        dbContext.Users.Remove(user);

        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<bool> TryMigrateGroup(MareDbContext context, Group group, string potentialNewOwnerUid, int maxGroupsByUser)
    {
        var newOwnerOwnedGroups = await context.Groups.CountAsync(g => g.OwnerUID == potentialNewOwnerUid).ConfigureAwait(false);
        if (newOwnerOwnedGroups >= maxGroupsByUser)
        {
            return false;
        }
        group.OwnerUID = potentialNewOwnerUid;
        group.Alias = null;
        await context.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }
}