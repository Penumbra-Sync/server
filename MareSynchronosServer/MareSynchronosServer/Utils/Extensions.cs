using MareSynchronos.API.Dto.Group;
using MareSynchronos.API.Dto.User;
using MareSynchronosShared.Models;

namespace MareSynchronosServer.Utils
{
    public static class Extensions
    {
        public static GroupData ToGroupData(this Group group)
        {
            return new GroupData(group.GID, group.Alias);
        }

        public static UserData ToUserData(this User user)
        {
            return new UserData(user.UID, user.Alias);
        }

        public static GroupPermissions GetGroupPermissions(this Group group)
        {
            GroupPermissions permissions = GroupPermissions.NoneSet;
            permissions ^= group.DisableAnimations ? GroupPermissions.DisableAnimations : GroupPermissions.NoneSet;
            permissions ^= group.DisableSounds ? GroupPermissions.DisableSounds : GroupPermissions.NoneSet;
            permissions ^= group.InvitesEnabled ? GroupPermissions.NoneSet : GroupPermissions.DisableInvites;
            return permissions;
        }

        public static GroupUserPermissions GetGroupPairPermissions(this GroupPair groupPair)
        {
            GroupUserPermissions permissions = GroupUserPermissions.NoneSet;
            permissions ^= groupPair.DisableAnimations ? GroupUserPermissions.DisableAnimations : GroupUserPermissions.NoneSet;
            permissions ^= groupPair.DisableSounds ? GroupUserPermissions.DisableSounds : GroupUserPermissions.NoneSet;
            permissions ^= groupPair.IsPaused ? GroupUserPermissions.Paused : GroupUserPermissions.NoneSet;
            return permissions;
        }

        public static GroupUserInfo GetGroupPairUserInfo(this GroupPair groupPair)
        {
            GroupUserInfo groupUserInfo = GroupUserInfo.None;
            groupUserInfo ^= groupPair.IsPinned ? GroupUserInfo.IsPinned : GroupUserInfo.None;
            groupUserInfo ^= groupPair.IsModerator ? GroupUserInfo.IsModerator : GroupUserInfo.None;
            return groupUserInfo;
        }
    }
}
