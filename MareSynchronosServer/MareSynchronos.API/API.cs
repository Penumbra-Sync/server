using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MareSynchronos.API
{
    public class Api
    {
        public const int Version = 5;
        public const string Path = "/mare";

        public const string SendFileAbortUpload = "AbortUpload";
        public const string InvokeFileSendFiles = "SendFiles";
        public const string InvokeFileIsUploadFinished = "IsUploadFinished";
        public const string SendFileUploadFileStreamAsync = "UploadFileStreamAsync";
        public const string InvokeFileGetFileSize = "GetFileSize";
        public const string StreamFileDownloadFileAsync = "StreamDownloadFileAsync";
        public const string SendFileDeleteAllFiles = "DeleteAllFiles";

        public const string InvokeHeartbeat = "Heartbeat";
        public const string InvokeGetSystemInfo = "GetSystemInfo";
        public const string OnUpdateSystemInfo = "OnUpdateSystemInfo";

        public const string InvokeAdminGetOnlineUsers = "AdminGetOnlineUsers";
        public const string InvokeAdminGetBannedUsers = "GetBannedUsers";
        public const string SendAdminUpdateOrAddBannedUser = "UpdateOrAddBannedUser";
        public const string SendAdminDeleteBannedUser = "DeleteBannedUser";
        public const string InvokeAdminGetForbiddenFiles = "GetForbiddenFiles";
        public const string SendAdminUpdateOrAddForbiddenFile = "UpdateOrAddForbiddenFile";
        public const string SendAdminDeleteForbiddenFile = "DeleteForbiddenFile";
        public const string SendAdminChangeModeratorStatus = "ChangeModeratorStatus";

        public const string OnAdminForcedReconnect = "OnForcedReconnect";
        public const string OnAdminUpdateOrAddBannedUser = "OnUpdateOrAddBannedUser";
        public const string OnAdminDeleteBannedUser = "OnDeleteBannedUser";
        public const string OnAdminUpdateOrAddForbiddenFile = "OnUpdateOrAddForbiddenFile";
        public const string OnAdminDeleteForbiddenFile = "OnDeleteForbiddenFile";

        public const string InvokeUserGetOnlineUsers = "GetOnlineUsers";
        public const string InvokeUserRegister = "Register";
        public const string InvokeUserPushCharacterDataToVisibleClients = "PushCharacterDataToVisibleClients";
        public const string InvokeUserGetOnlineCharacters = "GetOnlineCharacters";
        public const string SendUserPairedClientAddition = "SendPairedClientAddition";
        public const string SendUserPairedClientRemoval = "SendPairedClientRemoval";
        public const string SendUserPairedClientPauseChange = "SendPairedClientPauseChange";
        public const string InvokeUserGetPairedClients = "GetPairedClients";
        public const string SendUserDeleteAccount = "DeleteAccount";

        public const string OnUserUpdateClientPairs = "UpdateClientPairs";
        public const string OnUserReceiveCharacterData = "ReceiveCharacterData";
        public const string OnUserRemoveOnlinePairedPlayer = "RemoveOnlinePairedPlayer";
        public const string OnUserAddOnlinePairedPlayer = "AddOnlinePairedPlayer";
    }
}
