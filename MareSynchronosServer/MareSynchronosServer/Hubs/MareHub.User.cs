using System.Text.RegularExpressions;
using MareSynchronos.API;
using MareSynchronos.API.Dto.Group;
using MareSynchronos.API.Routes;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    [Authorize(Policy = "Identified")]
    public async Task UserDelete()
    {
        _logger.LogCallInfo();

        var userEntry = await _dbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        var ownPairData = await _dbContext.ClientPairs.Where(u => u.User.UID == UserUID).ToListAsync().ConfigureAwait(false);
        var auth = await _dbContext.Auth.SingleAsync(u => u.UserUID == UserUID).ConfigureAwait(false);
        var lodestone = await _dbContext.LodeStoneAuth.SingleOrDefaultAsync(a => a.User.UID == UserUID).ConfigureAwait(false);
        var groupPairs = await _dbContext.GroupPairs.Where(g => g.GroupUserUID == UserUID).ToListAsync().ConfigureAwait(false);

        if (lodestone != null)
        {
            _dbContext.Remove(lodestone);
        }

        while (_dbContext.Files.Any(f => f.Uploader == userEntry))
        {
            await Task.Delay(1000).ConfigureAwait(false);
        }

        _dbContext.ClientPairs.RemoveRange(ownPairData);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        var otherPairData = await _dbContext.ClientPairs.Include(u => u.User)
            .Where(u => u.OtherUser.UID == UserUID).AsNoTracking().ToListAsync().ConfigureAwait(false);
        foreach (var pair in otherPairData)
        {
            await Clients.User(pair.User.UID).Client_UserUpdateClientPairs(new ClientPairDto()
            {
                OtherUID = UserUID,
                IsRemoved = true,
            }).ConfigureAwait(false);
        }

        foreach (var pair in groupPairs)
        {
            await GroupLeave(new GroupDto(new GroupData(pair.GroupGID))).ConfigureAwait(false);
        }

        _mareMetrics.IncCounter(MetricsAPI.CounterUsersRegisteredDeleted, 1);

        _dbContext.ClientPairs.RemoveRange(otherPairData);
        _dbContext.Users.Remove(userEntry);
        _dbContext.Auth.Remove(auth);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<CharacterDto>> UserGetOnlineCharacters()
    {
        _logger.LogCallInfo();

        var usersToSendOnlineTo = await SendOnlineToAllPairedUsers(UserCharaIdent).ConfigureAwait(false);
        var idents = await GetIdentFromUidsFromRedis(usersToSendOnlineTo).ConfigureAwait(false);
        return idents.Where(i => !string.IsNullOrEmpty(i.Value)).Select(k => new CharacterDto(k.Key, k.Value, true)).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<ClientPairDto>> UserGetPairedClients()
    {
        _logger.LogCallInfo();

        var query =
            from userToOther in _dbContext.ClientPairs
            join otherToUser in _dbContext.ClientPairs
                on new
                {
                    user = userToOther.UserUID,
                    other = userToOther.OtherUserUID,

                } equals new
                {
                    user = otherToUser.OtherUserUID,
                    other = otherToUser.UserUID,
                } into leftJoin
            from otherEntry in leftJoin.DefaultIfEmpty()
            where
                userToOther.UserUID == UserUID
            select new
            {
                userToOther.OtherUser.Alias,
                userToOther.IsPaused,
                OtherIsPaused = otherEntry != null && otherEntry.IsPaused,
                userToOther.OtherUserUID,
                IsSynced = otherEntry != null,
            };

        return (await query.AsNoTracking().ToListAsync().ConfigureAwait(false)).Select(f => new ClientPairDto()
        {
            VanityUID = f.Alias,
            IsPaused = f.IsPaused,
            OtherUID = f.OtherUserUID,
            IsSynced = f.IsSynced,
            IsPausedFromOthers = f.OtherIsPaused,
        }).ToList();
    }

    [GeneratedRegex(@"^[A-Z0-9]{40}$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ECMAScript)]
    private static partial Regex HashRegex();

    [GeneratedRegex(@"^([a-z0-9_ '+&,\.\-\{\}]+\/)+([a-z0-9_ '+&,\.\-\{\}]+\.[a-z]{3,4})$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ECMAScript)]
    private static partial Regex GamePathRegex();

    private static readonly string[] AllowedExtensionsForGamePaths = { ".mdl", ".tex", ".mtrl", ".tmb", ".pap", ".avfx", ".atex", ".sklb", ".eid", ".phyb", ".scd", ".skp", ".shpk" };

    [Authorize(Policy = "Identified")]
    public async Task UserPushData(CharacterCacheDto characterCache, List<string> visibleCharacterIds)
    {
        _logger.LogCallInfo(MareHubLogger.Args(visibleCharacterIds.Count));

        bool hadInvalidData = false;
        List<string> invalidGamePaths = new();
        List<string> invalidFileSwapPaths = new();
        foreach (var replacement in characterCache.FileReplacements.SelectMany(p => p.Value))
        {
            var invalidPaths = replacement.GamePaths.Where(p => !GamePathRegex().IsMatch(p)).ToList();
            invalidPaths.AddRange(replacement.GamePaths.Where(p => !AllowedExtensionsForGamePaths.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase))));
            replacement.GamePaths = replacement.GamePaths.Where(p => !invalidPaths.Contains(p, StringComparer.OrdinalIgnoreCase)).ToArray();
            bool validGamePaths = replacement.GamePaths.Any();
            bool validHash = string.IsNullOrEmpty(replacement.Hash) || HashRegex().IsMatch(replacement.Hash);
            bool validFileSwapPath = string.IsNullOrEmpty(replacement.FileSwapPath) || GamePathRegex().IsMatch(replacement.FileSwapPath);
            if (!validGamePaths || !validHash || !validFileSwapPath)
            {
                _logger.LogCallWarning(MareHubLogger.Args("Invalid Data", "GamePaths", validGamePaths, string.Join(",", invalidPaths), "Hash", validHash, replacement.Hash, "FileSwap", validFileSwapPath, replacement.FileSwapPath));
                hadInvalidData = true;
                if (!validFileSwapPath) invalidFileSwapPaths.Add(replacement.FileSwapPath);
                if (!validGamePaths) invalidGamePaths.AddRange(replacement.GamePaths);
                if (!validHash) invalidFileSwapPaths.Add(replacement.Hash);
            }
        }

        if (hadInvalidData)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "One or more of your supplied mods were rejected from the server. Consult /xllog for more information.").ConfigureAwait(false);
            throw new HubException("Invalid data provided, contact the appropriate mod creator to resolve those issues"
            + Environment.NewLine
            + string.Join(Environment.NewLine, invalidGamePaths.Select(p => "Invalid Game Path: " + p))
            + Environment.NewLine
            + string.Join(Environment.NewLine, invalidFileSwapPaths.Select(p => "Invalid FileSwap Path: " + p)));
        }

        var allPairedUsers = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var idents = await GetIdentFromUidsFromRedis(allPairedUsers).ConfigureAwait(false);

        var allPairedUsersDict = idents
            .Where(f => visibleCharacterIds.Contains(f.Value, StringComparer.Ordinal));

        _logger.LogCallInfo(MareHubLogger.Args(visibleCharacterIds.Count, allPairedUsersDict.Count()));

        await Clients.Users(allPairedUsersDict.Select(f => f.Key)).Client_UserReceiveCharacterData(characterCache, UserUID).ConfigureAwait(false);

        _mareMetrics.IncCounter(MetricsAPI.CounterUserPushData);
        _mareMetrics.IncCounter(MetricsAPI.CounterUserPushDataTo, allPairedUsersDict.Count());
    }

    [Authorize(Policy = "Identified")]
    public async Task UserAddPair(string uid)
    {
        _logger.LogCallInfo(MareHubLogger.Args(uid));

        // don't allow adding yourself or nothing
        uid = uid.Trim();
        if (string.Equals(uid, UserUID, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(uid)) return;

        // grab other user, check if it exists and if a pair already exists
        var otherUser = await _dbContext.Users.SingleOrDefaultAsync(u => u.UID == uid || u.Alias == uid).ConfigureAwait(false);
        if (otherUser == null)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, $"Cannot pair with {uid}, UID does not exist").ConfigureAwait(false);
            return;
        }

        var existingEntry =
            await _dbContext.ClientPairs.AsNoTracking()
                .FirstOrDefaultAsync(p =>
                    p.User.UID == UserUID && p.OtherUserUID == otherUser.UID).ConfigureAwait(false);

        if (existingEntry != null)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, $"Cannot pair with {uid}, already paired").ConfigureAwait(false);
            return;
        }

        // grab self create new client pair and save
        var user = await _dbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(uid, "Success"));

        ClientPair wl = new ClientPair()
        {
            IsPaused = false,
            OtherUser = otherUser,
            User = user,
        };
        await _dbContext.ClientPairs.AddAsync(wl).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        // get the opposite entry of the client pair
        var otherEntry = OppositeEntry(otherUser.UID);
        await Clients.User(user.UID).Client_UserUpdateClientPairs(
            new ClientPairDto()
            {
                VanityUID = otherUser.Alias,
                OtherUID = otherUser.UID,
                IsPaused = false,
                IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                IsSynced = otherEntry != null,
            }).ConfigureAwait(false);

        // if there's no opposite entry do nothing
        if (otherEntry == null) return;

        // check if other user is online
        var otherIdent = await GetIdentFromUidFromRedis(otherUser.UID).ConfigureAwait(false);
        if (otherIdent == null) return;

        // send push with update to other user if other user is online
        await Clients.User(otherUser.UID).Client_UserUpdateClientPairs(
            new ClientPairDto()
            {
                VanityUID = user.Alias,
                OtherUID = user.UID,
                IsPaused = otherEntry.IsPaused,
                IsPausedFromOthers = false,
                IsSynced = true,
            }).ConfigureAwait(false);

        // get own ident and all pairs
        var userIdent = await GetIdentFromUidFromRedis(user.UID).ConfigureAwait(false);
        var allUserPairs = await GetAllPairedClientsWithPauseState().ConfigureAwait(false);

        // if the other user has paused the main user and there was no previous group connection don't send anything
        if (!otherEntry.IsPaused && allUserPairs.Any(p => string.Equals(p.UID, otherUser.UID, StringComparison.Ordinal) && p.IsPausedPerGroup is PauseInfo.Paused or PauseInfo.NoConnection))
        {
            await Clients.User(user.UID).Client_UserChangePairedPlayer(new CharacterDto(otherUser.UID, otherIdent, true)).ConfigureAwait(false);
            await Clients.User(otherUser.UID).Client_UserChangePairedPlayer(new CharacterDto(UserUID, userIdent, true)).ConfigureAwait(false);
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task UserChangePairPauseStatus(string otherUserUid, bool isPaused)
    {
        _logger.LogCallInfo(MareHubLogger.Args(otherUserUid, isPaused));

        if (string.Equals(otherUserUid, UserUID, StringComparison.Ordinal)) return;
        ClientPair pair = await _dbContext.ClientPairs.SingleOrDefaultAsync(w => w.UserUID == UserUID && w.OtherUserUID == otherUserUid).ConfigureAwait(false);
        if (pair == null) return;

        pair.IsPaused = isPaused;
        _dbContext.Update(pair);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(otherUserUid, isPaused, "Success"));

        var otherEntry = OppositeEntry(otherUserUid);

        await Clients.User(UserUID).Client_UserUpdateClientPairs(
            new ClientPairDto()
            {
                OtherUID = otherUserUid,
                IsPaused = isPaused,
                IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                IsSynced = otherEntry != null,
            }).ConfigureAwait(false);
        if (otherEntry != null)
        {
            await Clients.User(otherUserUid).Client_UserUpdateClientPairs(new ClientPairDto()
            {
                OtherUID = UserUID,
                IsPaused = otherEntry.IsPaused,
                IsPausedFromOthers = isPaused,
                IsSynced = true,
            }).ConfigureAwait(false);

            var otherCharaIdent = await GetIdentFromUidFromRedis(pair.OtherUserUID).ConfigureAwait(false);

            if (UserCharaIdent == null || otherCharaIdent == null || otherEntry.IsPaused) return;

            await Clients.User(UserUID).Client_UserChangePairedPlayer(new CharacterDto(otherUserUid, otherCharaIdent, !isPaused)).ConfigureAwait(false);
            await Clients.User(otherUserUid).Client_UserChangePairedPlayer(new CharacterDto(UserUID, UserCharaIdent, !isPaused)).ConfigureAwait(false);
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task UserRemovePair(string otherUserUid)
    {
        _logger.LogCallInfo(MareHubLogger.Args(otherUserUid));

        if (string.Equals(otherUserUid, UserUID, StringComparison.Ordinal)) return;

        // check if client pair even exists
        ClientPair callerPair =
            await _dbContext.ClientPairs.SingleOrDefaultAsync(w => w.UserUID == UserUID && w.OtherUserUID == otherUserUid).ConfigureAwait(false);
        bool callerHadPaused = callerPair.IsPaused;
        if (callerPair == null) return;

        // delete from database, send update info to users pair list
        _dbContext.ClientPairs.Remove(callerPair);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(otherUserUid, "Success"));

        await Clients.User(UserUID)
            .Client_UserUpdateClientPairs(new ClientPairDto()
            {
                OtherUID = otherUserUid,
                IsRemoved = true,
            }).ConfigureAwait(false);

        // check if opposite entry exists
        var oppositeClientPair = OppositeEntry(otherUserUid);
        if (oppositeClientPair == null) return;

        // check if other user is online, if no then there is no need to do anything further
        var otherIdent = await GetIdentFromUidFromRedis(otherUserUid).ConfigureAwait(false);
        if (otherIdent == null) return;

        // get own ident and 
        await Clients.User(otherUserUid).Client_UserUpdateClientPairs(
            new ClientPairDto()
            {
                OtherUID = UserUID,
                IsPausedFromOthers = false,
                IsSynced = false,
            }).ConfigureAwait(false);

        // if the other user had paused the user the state will be offline for either, do nothing
        bool otherHadPaused = oppositeClientPair.IsPaused;
        if (!callerHadPaused && otherHadPaused) return;

        var allUsers = await GetAllPairedClientsWithPauseState().ConfigureAwait(false);
        var pauseEntry = allUsers.SingleOrDefault(f => string.Equals(f.UID, otherUserUid, StringComparison.Ordinal));
        var isPausedInGroup = pauseEntry == null || pauseEntry.IsPausedPerGroup is PauseInfo.Paused or PauseInfo.NoConnection;

        // if neither user had paused each other and both are in unpaused groups, state will be online for both, do nothing
        if (!callerHadPaused && !otherHadPaused && !isPausedInGroup) return;

        // if neither user had paused each other and either is not in an unpaused group with each other, change state to offline
        if (!callerHadPaused && !otherHadPaused && isPausedInGroup)
        {
            await Clients.User(UserUID).Client_UserChangePairedPlayer(new(otherUserUid, otherIdent, false)).ConfigureAwait(false);
            await Clients.User(otherUserUid).Client_UserChangePairedPlayer(new(UserUID, UserCharaIdent, false)).ConfigureAwait(false);
        }

        // if the caller had paused other but not the other has paused the caller and they are in an unpaused group together, change state to online
        if (callerHadPaused && !otherHadPaused && !isPausedInGroup)
        {
            await Clients.User(UserUID).Client_UserChangePairedPlayer(new(otherUserUid, otherIdent, true)).ConfigureAwait(false);
            await Clients.User(otherUserUid).Client_UserChangePairedPlayer(new(UserUID, UserCharaIdent, true)).ConfigureAwait(false);
        }
    }

    private ClientPair OppositeEntry(string otherUID) =>
                                _dbContext.ClientPairs.AsNoTracking().SingleOrDefault(w => w.User.UID == otherUID && w.OtherUser.UID == UserUID);
}
