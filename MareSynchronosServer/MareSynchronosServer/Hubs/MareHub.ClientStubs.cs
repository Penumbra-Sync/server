using MareSynchronos.API;
using System.Threading.Tasks;
using System;

namespace MareSynchronosServer.Hubs
{
    public partial class MareHub
    {
        public Task Client_UserUpdateClientPairs(ClientPairDto clientPairDto)
        {
            throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        }

        public Task Client_UpdateSystemInfo(SystemInfoDto systemInfo)
        {
            throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        }

        public Task Client_UserReceiveCharacterData(CharacterCacheDto clientPairDto, string characterIdent)
        {
            throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        }

        public Task Client_UserChangePairedPlayer(string characterIdent, bool isOnline)
        {
            throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        }

        public Task Client_GroupChange(GroupDto groupDto)
        {
            throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        }

        public Task Client_GroupUserChange(GroupPairDto groupPairDto)
        {
            throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        }

        public Task Client_AdminForcedReconnect()
        {
            throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        }

        public Task Client_AdminDeleteBannedUser(BannedUserDto dto)
        {
            throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        }

        public Task Client_AdminDeleteForbiddenFile(ForbiddenFileDto dto)
        {
            throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        }

        public Task Client_AdminUpdateOrAddBannedUser(BannedUserDto dto)
        {
            throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        }

        public Task Client_AdminUpdateOrAddForbiddenFile(ForbiddenFileDto dto)
        {
            throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        }
    }
}
