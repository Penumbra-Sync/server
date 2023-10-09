using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MareSynchronos.API.Data;
using MareSynchronos.API.Data.Enum;
using MareSynchronos.API.Data.Extensions;
using MareSynchronos.API.Dto;
using MareSynchronos.API.Dto.User;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    private static readonly string[] AllowedExtensionsForGamePaths = { ".mdl", ".tex", ".mtrl", ".tmb", ".pap", ".avfx", ".atex", ".sklb", ".eid", ".phyb", ".scd", ".skp", ".shpk" };

    // TODO: needed, requires change
    [Authorize(Policy = "Identified")]
    public async Task UserAddPair(UserDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        // don't allow adding nothing
        var uid = dto.User.UID.Trim();
        if (string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(dto.User.UID)) return;

        // grab other user, check if it exists and if a pair already exists
        var otherUser = await _dbContext.Users.SingleOrDefaultAsync(u => u.UID == uid || u.Alias == uid).ConfigureAwait(false);
        if (otherUser == null)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, $"Cannot pair with {dto.User.UID}, UID does not exist").ConfigureAwait(false);
            return;
        }

        if (string.Equals(otherUser.UID, UserUID, StringComparison.Ordinal))
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, $"My god you can't pair with yourself why would you do that please stop").ConfigureAwait(false);
            return;
        }

        var existingEntry =
            await _dbContext.ClientPairs.AsNoTracking()
                .FirstOrDefaultAsync(p =>
                    p.User.UID == UserUID && p.OtherUserUID == otherUser.UID).ConfigureAwait(false);

        if (existingEntry != null)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, $"Cannot pair with {dto.User.UID}, already paired").ConfigureAwait(false);
            return;
        }

        // grab self create new client pair and save
        var user = await _dbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        ClientPair wl = new ClientPair()
        {
            OtherUser = otherUser,
            User = user,
        };
        await _dbContext.ClientPairs.AddAsync(wl).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        _cacheService.MarkAsStale(UserUID, otherUser.UID);

        var existingData = await _cacheService.GetPairData(UserUID, otherUser.UID).ConfigureAwait(false);

        var existingPermissions = existingData.OwnPermissions;
        if (existingPermissions == null || !existingPermissions.Sticky)
        {
            var ownDefaultPermissions = await _dbContext.UserDefaultPreferredPermissions.AsNoTracking().SingleOrDefaultAsync(f => f.UserUID == UserUID).ConfigureAwait(false);

            existingPermissions = new UserPermissionSet()
            {
                User = user,
                OtherUser = otherUser,
                DisableAnimations = ownDefaultPermissions.DisableIndividualAnimations,
                DisableSounds = ownDefaultPermissions.DisableIndividualSounds,
                DisableVFX = ownDefaultPermissions.DisableIndividualVFX,
                IsPaused = false,
                Sticky = ownDefaultPermissions.IndividualIsSticky
            };

            await _dbContext.Permissions.AddAsync(existingPermissions).ConfigureAwait(false);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        _cacheService.MarkAsStale(UserUID, otherUser.UID);

        // get the opposite entry of the client pair
        var otherEntry = OppositeEntry(otherUser.UID);
        var otherIdent = await GetUserIdent(otherUser.UID).ConfigureAwait(false);

        var otherPermissions = existingData?.OtherPermissions ?? null;

        var ownPerm = existingPermissions.ToUserPermissions(setSticky: true);
        var otherPerm = otherPermissions.ToUserPermissions();

        var userPairResponse = new UserPairDto(otherUser.ToUserData(),
            otherEntry == null ? IndividualPairStatus.OneSided : IndividualPairStatus.Bidirectional,
            ownPerm, otherPerm);

        await Clients.User(user.UID).Client_UserAddClientPair(userPairResponse).ConfigureAwait(false);

        // check if other user is online
        if (otherIdent == null || otherEntry == null) return;

        // send push with update to other user if other user is online
        await Clients.User(otherUser.UID)
            .Client_UserUpdateOtherPairPermissions(new UserPermissionsDto(user.ToUserData(),
            existingPermissions.ToUserPermissions())).ConfigureAwait(false);

        await Clients.User(otherUser.UID)
            .Client_UpdateUserIndividualPairStatusDto(new(user.ToUserData(), IndividualPairStatus.Bidirectional))
            .ConfigureAwait(false);

        if (!ownPerm.IsPaused() && !otherPerm.IsPaused())
        {
            await Clients.User(UserUID).Client_UserSendOnline(new(otherUser.ToUserData(), otherIdent)).ConfigureAwait(false);
            await Clients.User(otherUser.UID).Client_UserSendOnline(new(user.ToUserData(), UserCharaIdent)).ConfigureAwait(false);
        }
    }

    // needed, no adjustment
    [Authorize(Policy = "Identified")]
    public async Task UserDelete()
    {
        _logger.LogCallInfo();

        var userEntry = await _dbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        var secondaryUsers = await _dbContext.Auth.Include(u => u.User).Where(u => u.PrimaryUserUID == UserUID).Select(c => c.User).ToListAsync().ConfigureAwait(false);
        foreach (var user in secondaryUsers)
        {
            await DeleteUser(user).ConfigureAwait(false);
        }

        await DeleteUser(userEntry).ConfigureAwait(false);
    }

    // needed, no adjustment
    [Authorize(Policy = "Identified")]
    public async Task<List<OnlineUserIdentDto>> UserGetOnlinePairs()
    {
        _logger.LogCallInfo();

        var allPairedUsers = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var pairs = await GetOnlineUsers(allPairedUsers).ConfigureAwait(false);

        return pairs.Select(p => new OnlineUserIdentDto(new UserData(p.Key), p.Value)).ToList();
    }

    // todo: needed, requires adjustment for individual permissions, requires new dto
    [Authorize(Policy = "Identified")]
    public async Task<List<UserFullPairDto>> UserGetPairedClients()
    {
        _logger.LogCallInfo();

        var pairs = await _cacheService.GetAllPairs(UserUID).ConfigureAwait(false);
        return pairs.Select(p =>
        {
            return new UserFullPairDto(new UserData(p.Key, p.Value.Alias),
                p.Value.ToIndividualPairStatus(),
                p.Value.GIDs.Where(g => !string.Equals(g, Constants.IndividualKeyword, StringComparison.OrdinalIgnoreCase)).ToList(),
                p.Value.OwnPermissions.ToUserPermissions(setSticky: true),
                p.Value.OtherPermissions.ToUserPermissions());
        }).ToList();
    }

    // needed, no adjustment
    [Authorize(Policy = "Identified")]
    public async Task<UserProfileDto> UserGetProfile(UserDto user)
    {
        _logger.LogCallInfo(MareHubLogger.Args(user));

        var allUserPairs = await GetAllPairedUnpausedUsers().ConfigureAwait(false);

        if (!allUserPairs.Contains(user.User.UID, StringComparer.Ordinal) && !string.Equals(user.User.UID, UserUID, StringComparison.Ordinal))
        {
            return new UserProfileDto(user.User, false, null, null, "Due to the pause status you cannot access this users profile.");
        }

        var data = await _dbContext.UserProfileData.SingleOrDefaultAsync(u => u.UserUID == user.User.UID).ConfigureAwait(false);
        if (data == null) return new UserProfileDto(user.User, false, null, null, null);

        if (data.FlaggedForReport) return new UserProfileDto(user.User, true, null, null, "This profile is flagged for report and pending evaluation");
        if (data.ProfileDisabled) return new UserProfileDto(user.User, true, null, null, "This profile was permanently disabled");

        return new UserProfileDto(user.User, false, data.IsNSFW, data.Base64ProfileImage, data.UserDescription);
    }

    // needed, no adjustment
    [Authorize(Policy = "Identified")]
    public async Task UserPushData(UserCharaDataMessageDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto.CharaData.FileReplacements.Count));

        // check for honorific containing . and /
        try
        {
            var honorificJson = Encoding.Default.GetString(Convert.FromBase64String(dto.CharaData.HonorificData));
            var deserialized = JsonSerializer.Deserialize<JsonElement>(honorificJson);
            if (deserialized.TryGetProperty("Title", out var honorificTitle))
            {
                var title = honorificTitle.GetString().Normalize(NormalizationForm.FormKD);
                if (UrlRegex().IsMatch(title))
                {
                    await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your data was not pushed: The usage of URLs the Honorific titles is prohibited. Remove them to be able to continue to push data.").ConfigureAwait(false);
                    throw new HubException("Invalid data provided, Honorific title invalid: " + title);
                }
            }
        }
        catch (HubException)
        {
            throw;
        }
        catch (Exception)
        {
            // swallow
        }

        bool hadInvalidData = false;
        List<string> invalidGamePaths = new();
        List<string> invalidFileSwapPaths = new();
        foreach (var replacement in dto.CharaData.FileReplacements.SelectMany(p => p.Value))
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

        var allPairs = await _cacheService.GetAllPairs(UserUID).ConfigureAwait(false);
        allPairs.Where(p => !p.Value.OwnPermissions.IsPaused && p.Value.OtherPermissions != null && !p.Value.OtherPermissions.IsPaused).ToList();

        var allPairedUsers = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var idents = await GetOnlineUsers(allPairedUsers).ConfigureAwait(false);

        var recipients = allPairedUsers.Where(f => dto.Recipients.Select(r => r.UID).Contains(f, StringComparer.Ordinal)).ToList();

        _logger.LogCallInfo(MareHubLogger.Args(idents.Count, recipients.Count()));

        await Clients.Users(recipients).Client_UserReceiveCharacterData(new OnlineUserCharaDataDto(new UserData(UserUID), dto.CharaData)).ConfigureAwait(false);

        _mareMetrics.IncCounter(MetricsAPI.CounterUserPushData);
        _mareMetrics.IncCounter(MetricsAPI.CounterUserPushDataTo, recipients.Count());
    }

    // todo: verify this makes sense as written
    [Authorize(Policy = "Identified")]
    public async Task UserRemovePair(UserDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        if (string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal)) return;

        // check if client pair even exists
        ClientPair callerPair =
            await _dbContext.ClientPairs.SingleOrDefaultAsync(w => w.UserUID == UserUID && w.OtherUserUID == dto.User.UID).ConfigureAwait(false);
        if (callerPair == null) return;

        var pairData = await _cacheService.GetPairData(UserUID, dto.User.UID).ConfigureAwait(false);

        // delete from database, send update info to users pair list
        _dbContext.ClientPairs.Remove(callerPair);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        _cacheService.MarkAsStale(UserUID, dto.User.UID);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        await Clients.User(UserUID).Client_UserRemoveClientPair(dto).ConfigureAwait(false);

        // check if opposite entry exists
        if (!pairData.IsPaired) return;

        // check if other user is online, if no then there is no need to do anything further
        var otherIdent = await GetUserIdent(dto.User.UID).ConfigureAwait(false);
        if (otherIdent == null) return;

        // if the other user had paused the user the state will be offline for either, do nothing
        bool callerHadPaused = pairData.OwnPermissions?.IsPaused ?? false;

        // send updated individual pair status
        await Clients.User(dto.User.UID)
            .Client_UpdateUserIndividualPairStatusDto(new(new(UserUID), IndividualPairStatus.OneSided))
            .ConfigureAwait(false);

        UserPermissionSet? otherPermissions = pairData.OtherPermissions;
        bool otherHadPaused = otherPermissions?.IsPaused ?? true;

        // if the either had paused, do nothing
        if (callerHadPaused && otherHadPaused) return;

        //var userGroups = await _dbContext.GroupPairs.Where(u => u.GroupUserUID == UserUID).Select(g => g.GroupGID).ToListAsync().ConfigureAwait(false);
        //var otherUserGroups = await _dbContext.GroupPairs.Where(u => u.GroupUserUID == dto.User.UID).Select(g => g.GroupGID).ToListAsync().ConfigureAwait(false);
        //bool anyGroupsInCommon = userGroups.Exists(u => otherUserGroups.Contains(u, StringComparer.OrdinalIgnoreCase));
        var currentPairData = await _cacheService.GetPairData(UserUID, dto.User.UID).ConfigureAwait(false);

        // if neither user had paused each other and either is not in an unpaused group with each other, change state to offline
        if (!currentPairData?.IsSynced ?? true)
        {
            await Clients.User(UserUID).Client_UserSendOffline(dto).ConfigureAwait(false);
            await Clients.User(dto.User.UID).Client_UserSendOffline(new(new(UserUID))).ConfigureAwait(false);
        }
    }

    // needed, no adjustment
    [Authorize(Policy = "Identified")]
    public async Task UserReportProfile(UserProfileReportDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        UserProfileDataReport report = await _dbContext.UserProfileReports.SingleOrDefaultAsync(u => u.ReportedUserUID == dto.User.UID && u.ReportingUserUID == UserUID).ConfigureAwait(false);
        if (report != null)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "You already reported this profile and it's pending validation").ConfigureAwait(false);
            return;
        }

        UserProfileData profile = await _dbContext.UserProfileData.SingleOrDefaultAsync(u => u.UserUID == dto.User.UID).ConfigureAwait(false);
        if (profile == null)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "This user has no profile").ConfigureAwait(false);
            return;
        }

        UserProfileDataReport reportToAdd = new()
        {
            ReportDate = DateTime.UtcNow,
            ReportingUserUID = UserUID,
            ReportReason = dto.ProfileReport,
            ReportedUserUID = dto.User.UID,
        };

        profile.FlaggedForReport = true;

        await _dbContext.UserProfileReports.AddAsync(reportToAdd).ConfigureAwait(false);

        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        await Clients.User(dto.User.UID).Client_ReceiveServerMessage(MessageSeverity.Warning, "Your Mare profile has been reported and disabled for admin validation").ConfigureAwait(false);

        var allPairedUsers = await GetAllPairedUnpausedUsers(dto.User.UID).ConfigureAwait(false);
        var pairs = await GetOnlineUsers(allPairedUsers).ConfigureAwait(false);

        await Clients.Users(pairs.Select(p => p.Key)).Client_UserUpdateProfile(new(dto.User)).ConfigureAwait(false);
        await Clients.Users(dto.User.UID).Client_UserUpdateProfile(new(dto.User)).ConfigureAwait(false);
    }

    // todo: verify this makes sense as written
    [Authorize(Policy = "Identified")]
    public async Task UserSetPairPermissions(UserPermissionsDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        if (string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal)) return;
        UserPermissionSet prevPermissions = await _dbContext.Permissions.SingleOrDefaultAsync(w => w.UserUID == UserUID && w.OtherUserUID == dto.User.UID).ConfigureAwait(false);
        if (prevPermissions == null) return; // you always should have permissions to another user

        var pairData = await _cacheService.GetPairData(UserUID, dto.User.UID).ConfigureAwait(false);
        bool setSticky = false;
        if (!pairData.GIDs.Contains(Constants.IndividualKeyword, StringComparer.Ordinal))
        {
            if (!pairData.OwnPermissions.Sticky && !dto.Permissions.IsSticky())
            {
                var defaultPermissions = await _dbContext.UserDefaultPreferredPermissions.SingleAsync(u => u.UserUID == UserUID).ConfigureAwait(false);
                setSticky = defaultPermissions.IndividualIsSticky;
            }
        }

        var pauseChange = prevPermissions.IsPaused != dto.Permissions.IsPaused();

        prevPermissions.IsPaused = dto.Permissions.IsPaused();
        prevPermissions.DisableAnimations = dto.Permissions.IsDisableAnimations();
        prevPermissions.DisableSounds = dto.Permissions.IsDisableSounds();
        prevPermissions.DisableVFX = dto.Permissions.IsDisableVFX();
        prevPermissions.Sticky = dto.Permissions.IsSticky() || setSticky;
        _dbContext.Update(prevPermissions);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        _cacheService.MarkAsStale(UserUID, dto.User.UID);

        _logger.LogCallInfo(MareHubLogger.Args(dto, "Success"));

        var otherEntry = OppositeEntry(dto.User.UID);

        await Clients.User(UserUID).Client_UserUpdateSelfPairPermissions(dto).ConfigureAwait(false);

        if (otherEntry != null)
        {
            await Clients.User(dto.User.UID).Client_UserUpdateOtherPairPermissions(new UserPermissionsDto(new UserData(UserUID), dto.Permissions)).ConfigureAwait(false);

            if (pauseChange)
            {
                var otherCharaIdent = await GetUserIdent(dto.User.UID).ConfigureAwait(false);
                var otherPermissions = await _dbContext.Permissions.SingleOrDefaultAsync(w => w.UserUID == dto.User.UID && w.OtherUserUID == UserUID).ConfigureAwait(false);

                if (UserCharaIdent == null || otherCharaIdent == null || otherPermissions.IsPaused) return;

                if (dto.Permissions.IsPaused())
                {
                    await Clients.User(UserUID).Client_UserSendOffline(dto).ConfigureAwait(false);
                    await Clients.User(dto.User.UID).Client_UserSendOffline(new(new(UserUID))).ConfigureAwait(false);
                }
                else
                {
                    await Clients.User(UserUID).Client_UserSendOnline(new(dto.User, otherCharaIdent)).ConfigureAwait(false);
                    await Clients.User(dto.User.UID).Client_UserSendOnline(new(new(UserUID), UserCharaIdent)).ConfigureAwait(false);
                }
            }
        }
    }

    // needed, no adjustment
    [Authorize(Policy = "Identified")]
    public async Task UserSetProfile(UserProfileDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto));

        if (!string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal)) throw new HubException("Cannot modify profile data for anyone but yourself");

        var existingData = await _dbContext.UserProfileData.SingleOrDefaultAsync(u => u.UserUID == dto.User.UID).ConfigureAwait(false);

        if (existingData?.FlaggedForReport ?? false)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your profile is currently flagged for report and cannot be edited").ConfigureAwait(false);
            return;
        }

        if (existingData?.ProfileDisabled ?? false)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your profile was permanently disabled and cannot be edited").ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrEmpty(dto.ProfilePictureBase64))
        {
            byte[] imageData = Convert.FromBase64String(dto.ProfilePictureBase64);
            using MemoryStream ms = new(imageData);
            var format = await Image.DetectFormatAsync(ms).ConfigureAwait(false);
            if (!format.FileExtensions.Contains("png", StringComparer.OrdinalIgnoreCase))
            {
                await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your provided image file is not in PNG format").ConfigureAwait(false);
                return;
            }
            using var image = Image.Load<Rgba32>(imageData);

            if (image.Width > 256 || image.Height > 256 || (imageData.Length > 250 * 1024))
            {
                await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your provided image file is larger than 256x256 or more than 250KiB.").ConfigureAwait(false);
                return;
            }
        }

        if (existingData != null)
        {
            if (string.Equals("", dto.ProfilePictureBase64, StringComparison.OrdinalIgnoreCase))
            {
                existingData.Base64ProfileImage = null;
            }
            else if (dto.ProfilePictureBase64 != null)
            {
                existingData.Base64ProfileImage = dto.ProfilePictureBase64;
            }

            if (dto.IsNSFW != null)
            {
                existingData.IsNSFW = dto.IsNSFW.Value;
            }

            if (dto.Description != null)
            {
                existingData.UserDescription = dto.Description;
            }
        }
        else
        {
            UserProfileData userProfileData = new()
            {
                UserUID = dto.User.UID,
                Base64ProfileImage = dto.ProfilePictureBase64 ?? null,
                UserDescription = dto.Description ?? null,
                IsNSFW = dto.IsNSFW ?? false
            };

            await _dbContext.UserProfileData.AddAsync(userProfileData).ConfigureAwait(false);
        }

        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        var allPairedUsers = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var pairs = await GetOnlineUsers(allPairedUsers).ConfigureAwait(false);

        await Clients.Users(pairs.Select(p => p.Key)).Client_UserUpdateProfile(new(dto.User)).ConfigureAwait(false);
        await Clients.Caller.Client_UserUpdateProfile(new(dto.User)).ConfigureAwait(false);
    }

    // needed, no adjustment
    [Authorize(Policy = "Authenticated")]
    public async Task UserUpdateDefaultPermissions(DefaultPermissionsDto defaultPermissions)
    {
        _logger.LogCallInfo(MareHubLogger.Args(defaultPermissions));

        var permissions = await _dbContext.UserDefaultPreferredPermissions.SingleAsync(u => u.UserUID == UserUID).ConfigureAwait(false);

        permissions.DisableGroupAnimations = defaultPermissions.DisableGroupAnimations;
        permissions.DisableGroupSounds = defaultPermissions.DisableGroupSounds;
        permissions.DisableGroupVFX = defaultPermissions.DisableGroupVFX;
        permissions.DisableIndividualAnimations = defaultPermissions.DisableIndividualAnimations;
        permissions.DisableIndividualSounds = defaultPermissions.DisableIndividualSounds;
        permissions.DisableIndividualVFX = defaultPermissions.DisableIndividualVFX;
        permissions.IndividualIsSticky = defaultPermissions.IndividualIsSticky;

        _dbContext.Update(permissions);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

        await Clients.Caller.Client_UserUpdateDefaultPermissions(defaultPermissions).ConfigureAwait(false);
    }

    [GeneratedRegex(@"^([a-z0-9_ '+&,\.\-\{\}]+\/)+([a-z0-9_ '+&,\.\-\{\}]+\.[a-z]{3,4})$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ECMAScript)]
    private static partial Regex GamePathRegex();

    [GeneratedRegex(@"^[A-Z0-9]{40}$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ECMAScript)]
    private static partial Regex HashRegex();

    [GeneratedRegex("^[-a-zA-Z0-9@:%._\\+~#=]{1,256}[\\.,][a-zA-Z0-9()]{1,6}\\b(?:[-a-zA-Z0-9()@:%_\\+.~#?&\\/=]*)$")]
    private static partial Regex UrlRegex();

    private ClientPair OppositeEntry(string otherUID) =>
                                    _dbContext.ClientPairs.AsNoTracking().SingleOrDefault(w => w.User.UID == otherUID && w.OtherUser.UID == UserUID);
}