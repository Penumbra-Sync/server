using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MareSynchronos.API
{
    public class API
    {
        public const int Version = 2;
    }

    public class FilesHubAPI
    {
        public const string Path = "/files";

        public const string SendAbortUpload = "AbortUpload";
        public const string InvokeSendFiles = "SendFiles";
        public const string InvokeIsUploadFinished = "IsUploadFinished";
        public const string SendUploadFileStreamAsync = "UploadFileStreamAsync";
        public const string InvokeGetFileSize = "GetFileSize";
        public const string StreamDownloadFileAsync = "StreamDownloadFileAsync";
        public const string SendDeleteAllFiles = "DeleteAllFiles";
    }

    public class ConnectionHubAPI
    {
        public const string Path = "/heartbeat";
        public const string InvokeHeartbeat = "Heartbeat";
    }

    public class AdminHubAPI
    {
        public const string Path = "/admin";

        public const string InvokeGetOnlineUsers = "GetOnlineUsers";
        public const string InvokeGetBannedUsers = "GetBannedUsers";
        public const string SendUpdateOrAddBannedUser = "UpdateOrAddBannedUser";
        public const string SendDeleteBannedUser = "DeleteBannedUser";
        public const string InvokeGetForbiddenFiles = "GetForbiddenFiles";
        public const string SendUpdateOrAddForbiddenFile = "UpdateOrAddForbiddenFile";
        public const string SendDeleteForbiddenFile = "DeleteForbiddenFile";
        public const string SendChangeModeratorStatus = "ChangeModeratorStatus";

        public const string OnForcedReconnect = "ForcedReconnect";
        public const string OnUpdateOrAddBannedUser = "UpdateOrAddBannedUser";
        public const string OnDeleteBannedUser = "DeleteBannedUser";
        public const string OnUpdateOrAddForbiddenFile = "UpdateOrAddForbiddenFile";
        public const string OnDeleteForbiddenFile = "DeleteForbiddenFile";
    }

    public class UserHubAPI
    {
        public const string Path = "/user";

        public const string InvokeGetOnlineUsers = "GetOnlineUsers";
        public const string InvokeRegister = "Register";
        public const string InvokePushCharacterDataToVisibleClients = "PushCharacterDataToVisibleClients";
        public const string InvokeGetOnlineCharacters = "GetOnlineCharacters";
        public const string SendPairedClientAddition = "SendPairedClientAddition";
        public const string SendPairedClientRemoval = "SendPairedClientRemoval";
        public const string SendPairedClientPauseChange = "SendPairedClientPauseChange";
        public const string InvokeGetPairedClients = "GetPairedClients";
        public const string SendDeleteAccount = "DeleteAccount";

        public const string OnUsersOnline = "UsersOnline";
        public const string OnUpdateClientPairs = "UpdateClientPairs";
        public const string OnReceiveCharacterData = "ReceiveCharacterData";
        public const string OnRemoveOnlinePairedPlayer = "RemoveOnlinePairedPlayer";
        public const string OnAddOnlinePairedPlayer = "AddOnlinePairedPlayer";
    }
}
