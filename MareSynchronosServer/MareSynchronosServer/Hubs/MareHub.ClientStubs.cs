using MareSynchronos.API;
using MareSynchronos.API.Dto.Group;
using MareSynchronos.API.Routes;

namespace MareSynchronosServer.Hubs
{
    public partial class MareHub
    {
        public Task Client_UserUpdateClientPairs(ClientPairDto clientPairDto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_UpdateSystemInfo(SystemInfoDto systemInfo) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_UserReceiveCharacterData(CharacterCacheDto clientPairDto, string uid) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_UserChangePairedPlayer(CharacterDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_AdminForcedReconnect() => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_AdminDeleteBannedUser(BannedUserDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_AdminDeleteForbiddenFile(ForbiddenFileDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_AdminUpdateOrAddBannedUser(BannedUserDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_AdminUpdateOrAddForbiddenFile(ForbiddenFileDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_ReceiveServerMessage(MessageSeverity messageSeverity, string message) => throw new PlatformNotSupportedException("Calling clientside method on server not supported"); 
        public Task Client_DownloadReady(Guid requestId) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GroupSendFullInfo(GroupFullInfoDto groupInfo) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GroupDelete(GroupDto groupDto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GroupPairJoined(GroupPairFullInfoDto groupPairInfoDto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GroupPairLeft(GroupPairDto groupPairDto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GroupChangePermissions(GroupPermissionDto groupPermission) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GroupPairChangePermissions(GroupPairUserPermissionDto permissionDto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GroupSendInfo(GroupInfoDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GroupPairChangeUserInfo(GroupPairUserInfoDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
    }
}
