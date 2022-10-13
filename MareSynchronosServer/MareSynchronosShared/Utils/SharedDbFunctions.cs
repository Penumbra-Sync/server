using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosShared.Utils;

public static class SharedDbFunctions
{
    public static async Task<(bool, string)> MigrateOrDeleteGroup(MareDbContext context, Group group, List<GroupPair> groupPairs, int maxGroupsByUser)
    {
        bool groupHasMigrated = false;
        string newOwner = string.Empty;
        foreach (var potentialNewOwner in groupPairs)
        {
            groupHasMigrated = await TryMigrateGroup(context, group, potentialNewOwner.GroupUserUID, maxGroupsByUser).ConfigureAwait(false);

            if (groupHasMigrated)
            {
                newOwner = potentialNewOwner.GroupUserUID;
                potentialNewOwner.IsPinned = true;
                potentialNewOwner.IsModerator = false;

                await context.SaveChangesAsync().ConfigureAwait(false);
                break;
            }
        }

        if (!groupHasMigrated)
        {
            context.GroupPairs.RemoveRange(groupPairs);
            context.Groups.Remove(group);

            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        return (groupHasMigrated, newOwner);
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
