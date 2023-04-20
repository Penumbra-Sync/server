using MareSynchronos.API.Data;
using MareSynchronos.API.Data.Enum;
using MareSynchronos.API.Data.Extensions;
using MareSynchronosShared.Models;

namespace MareSynchronosServer.Utils
{
    public static class Extensions
    {
        public static GroupData ToGroupData(this Group group)
        {
            return new GroupData(group.GID, group.Alias);
        }

        public static UserData ToUserData(this GroupPair pair)
        {
            return new UserData(pair.GroupUser.UID, pair.GroupUser.Alias);
        }

        public static UserData ToUserData(this User user)
        {
            return new UserData(user.UID, user.Alias);
        }

        public static GroupPermissions GetGroupPermissions(this Group group)
        {
            var permissions = GroupPermissions.NoneSet;
            permissions.SetDisableAnimations(group.DisableAnimations);
            permissions.SetDisableSounds(group.DisableSounds);
            permissions.SetDisableInvites(!group.InvitesEnabled);
            permissions.SetDisableVFX(group.DisableVFX);
            return permissions;
        }

        public static GroupUserPermissions GetGroupPairPermissions(this GroupPair groupPair)
        {
            var permissions = GroupUserPermissions.NoneSet;
            permissions.SetDisableAnimations(groupPair.DisableAnimations);
            permissions.SetDisableSounds(groupPair.DisableSounds);
            permissions.SetPaused(groupPair.IsPaused);
            permissions.SetDisableVFX(groupPair.DisableVFX);
            return permissions;
        }

        public static GroupUserInfo GetGroupPairUserInfo(this GroupPair groupPair)
        {
            var groupUserInfo = GroupUserInfo.None;
            groupUserInfo.SetPinned(groupPair.IsPinned);
            groupUserInfo.SetModerator(groupPair.IsModerator);
            return groupUserInfo;
        }
    }
}
