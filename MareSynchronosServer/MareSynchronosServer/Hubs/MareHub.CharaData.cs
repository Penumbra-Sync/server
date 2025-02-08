using MareSynchronos.API.Data;
using MareSynchronos.API.Dto.CharaData;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Models;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    [Authorize(Policy = "Identified")]
    public async Task<CharaDataFullDto?> CharaDataCreate()
    {
        _logger.LogCallInfo();

        int uploadCount = DbContext.CharaData.Count(c => c.UploaderUID == UserUID);
        User user = DbContext.Users.Single(u => u.UID == UserUID);
        int maximumUploads = string.IsNullOrEmpty(user.Alias) ? _maxCharaDataByUser : _maxCharaDataByUserVanity;
        if (uploadCount >= maximumUploads)
        {
            return null;
        }

        string charaDataId = null;
        while (charaDataId == null)
        {
            charaDataId = StringUtils.GenerateRandomString(10, "abcdefghijklmnopqrstuvwxyzABCDEFHIJKLMNOPQRSTUVWXYZ");
            bool idExists = await DbContext.CharaData.AnyAsync(c => c.UploaderUID == UserUID && c.Id == charaDataId).ConfigureAwait(false);
            if (idExists)
            {
                charaDataId = null;
            }
        }

        DateTime createdDate = DateTime.UtcNow;
        CharaData charaData = new()
        {
            Id = charaDataId,
            UploaderUID = UserUID,
            CreatedDate = createdDate,
            UpdatedDate = createdDate,
            AccessType = CharaDataAccess.Individuals,
            ShareType = CharaDataShare.Private,
            CustomizeData = string.Empty,
            GlamourerData = string.Empty,
            ExpiryDate = DateTime.MaxValue,
            Description = string.Empty,
        };

        await DbContext.CharaData.AddAsync(charaData).ConfigureAwait(false);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args("SUCCESS", charaDataId));

        return GetCharaDataFullDto(charaData);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> CharaDataDelete(string id)
    {
        var existingData = await DbContext.CharaData.SingleOrDefaultAsync(u => u.Id == id && u.UploaderUID == UserUID).ConfigureAwait(false);
        if (existingData == null)
            return false;

        try
        {
            _logger.LogCallInfo(MareHubLogger.Args("SUCCESS", id));

            DbContext.Remove(existingData);
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(MareHubLogger.Args("FAILURE", id, ex.Message));
            return false;
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task<CharaDataDownloadDto?> CharaDataDownload(string id)
    {
        CharaData charaData = await GetCharaDataById(id, nameof(CharaDataDownload)).ConfigureAwait(false);

        if (!string.Equals(charaData.UploaderUID, UserUID, StringComparison.Ordinal))
        {
            charaData.DownloadCount++;
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        _logger.LogCallInfo(MareHubLogger.Args("SUCCESS", id));

        return GetCharaDataDownloadDto(charaData);
    }

    [Authorize(Policy = "Identified")]
    public async Task<CharaDataMetaInfoDto?> CharaDataGetMetainfo(string id)
    {
        var charaData = await GetCharaDataById(id, nameof(CharaDataGetMetainfo)).ConfigureAwait(false);

        _logger.LogCallInfo(MareHubLogger.Args("SUCCESS", id));

        return GetCharaDataMetaInfoDto(charaData);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<CharaDataFullDto>> CharaDataGetOwn()
    {
        var ownCharaData = await DbContext.CharaData
            .Include(u => u.Files)
            .Include(u => u.FileSwaps)
            .Include(u => u.OriginalFiles)
            .Include(u => u.AllowedIndividiuals)
            .ThenInclude(u => u.AllowedUser)
            .Include(u => u.AllowedIndividiuals)
            .ThenInclude(u => u.AllowedGroup)
            .Include(u => u.Poses)
            .AsSplitQuery()
            .Where(c => c.UploaderUID == UserUID).ToListAsync().ConfigureAwait(false);
        _logger.LogCallInfo(MareHubLogger.Args("SUCCESS"));
        return [.. ownCharaData.Select(GetCharaDataFullDto)];
    }

    [Authorize(Policy = "Identified")]
    public async Task<CharaDataFullDto?> CharaDataAttemptRestore(string id)
    {
        _logger.LogCallInfo(MareHubLogger.Args(id));
        var charaData = await DbContext.CharaData
            .Include(u => u.Files)
            .Include(u => u.FileSwaps)
            .Include(u => u.OriginalFiles)
            .Include(u => u.AllowedIndividiuals)
            .ThenInclude(u => u.AllowedUser)
            .Include(u => u.AllowedIndividiuals)
            .ThenInclude(u => u.AllowedGroup)
            .Include(u => u.Poses)
            .AsSplitQuery()
            .SingleOrDefaultAsync(s => s.Id == id && s.UploaderUID == UserUID)
            .ConfigureAwait(false);
        if (charaData == null)
            return null;

        var currentHashes = charaData.Files.Select(f => f.FileCacheHash).ToList();
        var missingFiles = charaData.OriginalFiles.Where(c => !currentHashes.Contains(c.Hash, StringComparer.Ordinal)).ToList();

        // now let's see what's on the db still
        var existingDbFiles = await DbContext.Files
            .Where(f => missingFiles.Select(k => k.Hash).Distinct().Contains(f.Hash))
            .ToListAsync()
            .ConfigureAwait(false);

        // now shove it all back into the db
        foreach (var dbFile in existingDbFiles)
        {
            var missingFileEntry = missingFiles.First(f => string.Equals(f.Hash, dbFile.Hash, StringComparison.Ordinal));
            charaData.Files.Add(new CharaDataFile()
            {
                FileCache = dbFile,
                GamePath = missingFileEntry.GamePath,
                Parent = charaData
            });
            missingFiles.Remove(missingFileEntry);
        }

        if (existingDbFiles.Any())
        {
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        return GetCharaDataFullDto(charaData);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<CharaDataMetaInfoDto>> CharaDataGetShared()
    {
        _logger.LogCallInfo();

        List<CharaData> sharedCharaData = [];
        var groups = await DbContext.GroupPairs
            .Where(u => u.GroupUserUID == UserUID)
            .Select(k => k.GroupGID)
            .AsNoTracking()
            .ToListAsync()
            .ConfigureAwait(false);

        var pairs = (await GetAllPairInfo(UserUID).ConfigureAwait(false));
        var individualPairs = pairs.Where(p => p.Value.IndividuallyPaired && (!p.Value.OwnPermissions?.IsPaused ?? false) && (!p.Value.OtherPermissions?.IsPaused ?? false)).Select(k => k.Key).ToList();
        var allPairs = pairs.Where(p => (!p.Value.OwnPermissions?.IsPaused ?? false) && (!p.Value.OtherPermissions?.IsPaused ?? false)).Select(k => k.Key).ToList();

        var allSharedDataByPair = await DbContext.CharaData
            .Include(u => u.Files)
            .Include(u => u.OriginalFiles)
            .Include(u => u.AllowedIndividiuals)
            .Include(u => u.Poses)
            .Include(u => u.Uploader)
            .Where(p => p.UploaderUID != UserUID && p.ShareType == CharaDataShare.Shared)
            .Where(p =>
                (individualPairs.Contains(p.UploaderUID) && p.AccessType == CharaDataAccess.ClosePairs)
                || (allPairs.Contains(p.UploaderUID) && (p.AccessType == CharaDataAccess.AllPairs || p.AccessType == CharaDataAccess.Public))
                || (p.AllowedIndividiuals.Any(u => u.AllowedUserUID == UserUID || (u.AllowedGroupGID != null && groups.Contains(u.AllowedGroupGID)))))
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync()
            .ConfigureAwait(false);


        foreach (var charaData in allSharedDataByPair)
        {
            sharedCharaData.Add(charaData);
        }

        _logger.LogCallInfo(MareHubLogger.Args("SUCCESS", sharedCharaData.Count));

        return [.. sharedCharaData.Select(GetCharaDataMetaInfoDto)];
    }

    [Authorize(Policy = "Identified")]
    public async Task<CharaDataFullDto?> CharaDataUpdate(CharaDataUpdateDto updateDto)
    {
        var charaData = await DbContext.CharaData
            .Include(u => u.Files)
            .Include(u => u.OriginalFiles)
            .Include(u => u.AllowedIndividiuals)
            .ThenInclude(u => u.AllowedUser)
            .Include(u => u.AllowedIndividiuals)
            .ThenInclude(u => u.AllowedGroup)
            .Include(u => u.FileSwaps)
            .Include(u => u.Poses)
            .AsSplitQuery()
            .SingleOrDefaultAsync(u => u.Id == updateDto.Id && u.UploaderUID == UserUID).ConfigureAwait(false);

        if (charaData == null)
            return null;

        bool anyChanges = false;

        if (updateDto.Description != null)
        {
            charaData.Description = updateDto.Description;
            anyChanges = true;
        }

        if (updateDto.ExpiryDate != null)
        {
            charaData.ExpiryDate = updateDto.ExpiryDate;
            anyChanges = true;
        }

        if (updateDto.GlamourerData != null)
        {
            charaData.GlamourerData = updateDto.GlamourerData;
            anyChanges = true;
        }

        if (updateDto.CustomizeData != null)
        {
            charaData.CustomizeData = updateDto.CustomizeData;
            anyChanges = true;
        }

        if (updateDto.ManipulationData != null)
        {
            charaData.ManipulationData = updateDto.ManipulationData;
            anyChanges = true;
        }

        if (updateDto.AccessType != null)
        {
            charaData.AccessType = GetAccessType(updateDto.AccessType.Value);
            anyChanges = true;
        }

        if (updateDto.ShareType != null)
        {
            charaData.ShareType = GetShareType(updateDto.ShareType.Value);
            anyChanges = true;
        }

        if (updateDto.AllowedUsers != null)
        {
            var individuals = charaData.AllowedIndividiuals.Where(k => k.AllowedGroup == null).ToList();
            var allowedUserList = updateDto.AllowedUsers.ToList();
            foreach (var user in updateDto.AllowedUsers)
            {
                if (charaData.AllowedIndividiuals.Any(k => k.AllowedUser != null && (string.Equals(k.AllowedUser.UID, user, StringComparison.Ordinal) || string.Equals(k.AllowedUser.Alias, user, StringComparison.Ordinal))))
                {
                    continue;
                }
                else
                {
                    var dbUser = await DbContext.Users.SingleOrDefaultAsync(u => u.UID == user || u.Alias == user).ConfigureAwait(false);
                    if (dbUser != null)
                    {
                        charaData.AllowedIndividiuals.Add(new CharaDataAllowance()
                        {
                            AllowedUser = dbUser,
                            Parent = charaData
                        });
                    }
                }
            }

            foreach (var dataUser in individuals.Where(k => !updateDto.AllowedUsers.Contains(k.AllowedUser.UID, StringComparer.Ordinal) && !updateDto.AllowedUsers.Contains(k.AllowedUser.Alias, StringComparer.Ordinal)))
            {
                DbContext.Remove(dataUser);
                charaData.AllowedIndividiuals.Remove(dataUser);
            }

            anyChanges = true;
        }

        if (updateDto.AllowedGroups != null)
        {
            var individualGroups = charaData.AllowedIndividiuals.Where(k => k.AllowedUser == null).ToList();
            var allowedGroups = updateDto.AllowedGroups.ToList();
            foreach (var group in updateDto.AllowedGroups)
            {
                if (charaData.AllowedIndividiuals.Any(k => k.AllowedGroup != null && (string.Equals(k.AllowedGroup.GID, group, StringComparison.Ordinal) || string.Equals(k.AllowedGroup.Alias, group, StringComparison.Ordinal))))
                {
                    continue;
                }
                else
                {
                    var groupUser = await DbContext.GroupPairs.Include(u => u.Group).SingleOrDefaultAsync(u => (u.Group.GID == group || u.Group.Alias == group) && u.GroupUserUID == UserUID).ConfigureAwait(false);
                    if (groupUser != null)
                    {
                        charaData.AllowedIndividiuals.Add(new CharaDataAllowance()
                        {
                            AllowedGroup = groupUser.Group,
                            Parent = charaData
                        });
                    }
                }
            }

            foreach (var dataGroup in individualGroups.Where(k => !updateDto.AllowedGroups.Contains(k.AllowedGroup.GID, StringComparer.Ordinal) && !updateDto.AllowedGroups.Contains(k.AllowedGroup.Alias, StringComparer.Ordinal)))
            {
                DbContext.Remove(dataGroup);
                charaData.AllowedIndividiuals.Remove(dataGroup);
            }

            anyChanges = true;
        }

        if (updateDto.FileGamePaths != null)
        {
            var originalFiles = charaData.OriginalFiles.ToList();
            charaData.OriginalFiles.Clear();
            DbContext.RemoveRange(originalFiles);
            var files = charaData.Files.ToList();
            charaData.Files.Clear();
            DbContext.RemoveRange(files);
            foreach (var file in updateDto.FileGamePaths)
            {
                charaData.Files.Add(new CharaDataFile()
                {
                    FileCacheHash = file.HashOrFileSwap,
                    GamePath = file.GamePath,
                    Parent = charaData
                });

                charaData.OriginalFiles.Add(new CharaDataOriginalFile()
                {
                    Hash = file.HashOrFileSwap,
                    Parent = charaData,
                    GamePath = file.GamePath
                });
            }

            anyChanges = true;
        }

        if (updateDto.FileSwaps != null)
        {
            var fileSwaps = charaData.FileSwaps.ToList();
            charaData.FileSwaps.Clear();
            DbContext.RemoveRange(fileSwaps);
            foreach (var file in updateDto.FileSwaps)
            {
                charaData.FileSwaps.Add(new CharaDataFileSwap()
                {
                    FilePath = file.HashOrFileSwap,
                    GamePath = file.GamePath,
                    Parent = charaData
                });
            }

            anyChanges = true;
        }

        if (updateDto.Poses != null)
        {
            foreach (var pose in updateDto.Poses)
            {
                if (pose.Id == null)
                {
                    charaData.Poses.Add(new CharaDataPose()
                    {
                        Description = pose.Description,
                        Parent = charaData,
                        ParentUploaderUID = UserUID,
                        PoseData = pose.PoseData,
                        WorldData = pose.WorldData == null ? string.Empty : JsonSerializer.Serialize(pose.WorldData),
                    });

                    anyChanges = true;
                }
                else
                {
                    var associatedPose = charaData.Poses.FirstOrDefault(p => p.Id == pose.Id);
                    if (associatedPose == null)
                        continue;

                    if (pose.Description == null && pose.PoseData == null && pose.WorldData == null)
                    {
                        charaData.Poses.Remove(associatedPose);
                        DbContext.Remove(associatedPose);
                    }
                    else
                    {
                        if (pose.Description != null)
                            associatedPose.Description = pose.Description;
                        if (pose.WorldData != null)
                        {
                            if (pose.WorldData.Value == default) associatedPose.WorldData = string.Empty;
                            else associatedPose.WorldData = JsonSerializer.Serialize(pose.WorldData.Value);
                        }
                        if (pose.PoseData != null)
                            associatedPose.PoseData = pose.PoseData;
                    }

                    anyChanges = true;
                }

                var overflowingPoses = charaData.Poses.Skip(10).ToList();
                foreach (var overflowing in overflowingPoses)
                {
                    charaData.Poses.Remove(overflowing);
                    DbContext.Remove(overflowing);
                }
            }
        }

        if (anyChanges)
        {
            charaData.UpdatedDate = DateTime.UtcNow;
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
            _logger.LogCallInfo(MareHubLogger.Args("SUCCESS", anyChanges));
        }

        return GetCharaDataFullDto(charaData);
    }

    private static CharaDataAccess GetAccessType(AccessTypeDto dataAccess) => dataAccess switch
    {
        AccessTypeDto.Public => CharaDataAccess.Public,
        AccessTypeDto.AllPairs => CharaDataAccess.AllPairs,
        AccessTypeDto.ClosePairs => CharaDataAccess.ClosePairs,
        AccessTypeDto.Individuals => CharaDataAccess.Individuals,
        _ => throw new NotSupportedException(),
    };

    private static AccessTypeDto GetAccessTypeDto(CharaDataAccess dataAccess) => dataAccess switch
    {
        CharaDataAccess.Public => AccessTypeDto.Public,
        CharaDataAccess.AllPairs => AccessTypeDto.AllPairs,
        CharaDataAccess.ClosePairs => AccessTypeDto.ClosePairs,
        CharaDataAccess.Individuals => AccessTypeDto.Individuals,
        _ => throw new NotSupportedException(),
    };

    private static CharaDataDownloadDto GetCharaDataDownloadDto(CharaData charaData)
    {
        return new CharaDataDownloadDto(charaData.Id, charaData.Uploader.ToUserData())
        {
            CustomizeData = charaData.CustomizeData,
            Description = charaData.Description,
            FileGamePaths = charaData.Files.Select(k => new GamePathEntry(k.FileCacheHash, k.GamePath)).ToList(),
            GlamourerData = charaData.GlamourerData,
            FileSwaps = charaData.FileSwaps.Select(k => new GamePathEntry(k.FilePath, k.GamePath)).ToList(),
            ManipulationData = charaData.ManipulationData,
        };
    }

    private CharaDataFullDto GetCharaDataFullDto(CharaData charaData)
    {
        return new CharaDataFullDto(charaData.Id, new(UserUID))
        {
            AccessType = GetAccessTypeDto(charaData.AccessType),
            ShareType = GetShareTypeDto(charaData.ShareType),
            AllowedUsers = [.. charaData.AllowedIndividiuals.Where(k => !string.IsNullOrEmpty(k.AllowedUserUID)).Select(u => new UserData(u.AllowedUser.UID, u.AllowedUser.Alias))],
            AllowedGroups = [.. charaData.AllowedIndividiuals.Where(k => !string.IsNullOrEmpty(k.AllowedGroupGID)).Select(k => new GroupData(k.AllowedGroup.GID, k.AllowedGroup.Alias))],
            CustomizeData = charaData.CustomizeData,
            Description = charaData.Description,
            ExpiryDate = charaData.ExpiryDate ?? DateTime.MaxValue,
            OriginalFiles = charaData.OriginalFiles.Select(k => new GamePathEntry(k.Hash, k.GamePath)).ToList(),
            FileGamePaths = charaData.Files.Select(k => new GamePathEntry(k.FileCacheHash, k.GamePath)).ToList(),
            FileSwaps = charaData.FileSwaps.Select(k => new GamePathEntry(k.FilePath, k.GamePath)).ToList(),
            GlamourerData = charaData.GlamourerData,
            CreatedDate = charaData.CreatedDate,
            UpdatedDate = charaData.UpdatedDate,
            ManipulationData = charaData.ManipulationData,
            DownloadCount = charaData.DownloadCount,
            PoseData = [.. charaData.Poses.OrderBy(p => p.Id).Select(k =>
            {
                WorldData data = default;

                if(!string.IsNullOrEmpty(k.WorldData)) data = JsonSerializer.Deserialize<WorldData>(k.WorldData);
                return new PoseEntry(k.Id)
                {
                    Description = k.Description,
                    PoseData = k.PoseData,
                    WorldData = data
                };
            })],
        };
    }

    private static CharaDataMetaInfoDto GetCharaDataMetaInfoDto(CharaData charaData)
    {
        var allOrigHashes = charaData.OriginalFiles.Select(k => k.Hash).ToList();
        var allFileHashes = charaData.Files.Select(f => f.FileCacheHash).ToList();
        var allHashesPresent = allOrigHashes.TrueForAll(h => allFileHashes.Contains(h, StringComparer.Ordinal));
        var canBeDownloaded = allHashesPresent &= !string.IsNullOrEmpty(charaData.GlamourerData);
        return new CharaDataMetaInfoDto(charaData.Id, charaData.Uploader.ToUserData())
        {
            CanBeDownloaded = canBeDownloaded,
            Description = charaData.Description,
            UpdatedDate = charaData.UpdatedDate,
            PoseData = [.. charaData.Poses.OrderBy(p => p.Id).Select(k =>
            {
                WorldData data = default;
                if(!string.IsNullOrEmpty(k.WorldData)) data = JsonSerializer.Deserialize<WorldData>(k.WorldData);
                return new PoseEntry(k.Id)
                {
                    Description = k.Description,
                    PoseData = k.PoseData,
                    WorldData = data
                };
            })],
        };
    }

    private static CharaDataShare GetShareType(ShareTypeDto dataShare) => dataShare switch
    {
        ShareTypeDto.Shared => CharaDataShare.Shared,
        ShareTypeDto.Private => CharaDataShare.Private,
        _ => throw new NotSupportedException(),
    };

    private static ShareTypeDto GetShareTypeDto(CharaDataShare dataShare) => dataShare switch
    {
        CharaDataShare.Shared => ShareTypeDto.Shared,
        CharaDataShare.Private => ShareTypeDto.Private,
        _ => throw new NotSupportedException(),
    };

    private async Task<bool> CheckCharaDataAllowance(CharaData charaData, List<string> joinedGroups)
    {
        // check for self
        if (string.Equals(charaData.UploaderUID, UserUID, StringComparison.Ordinal))
            return true;

        // check for public access
        if (charaData.AccessType == CharaDataAccess.Public)
            return true;

        // check for individuals
        if (charaData.AllowedIndividiuals.Any(u => string.Equals(u.AllowedUserUID, UserUID, StringComparison.Ordinal)))
            return true;

        if (charaData.AllowedIndividiuals.Any(u => joinedGroups.Contains(u.AllowedGroupGID, StringComparer.Ordinal)))
            return true;

        var pairInfoUploader = await GetAllPairInfo(charaData.UploaderUID).ConfigureAwait(false);

        // check for all pairs
        if (charaData.AccessType == CharaDataAccess.AllPairs)
        {
            if (pairInfoUploader.TryGetValue(UserUID, out var userInfo) && userInfo.IsSynced && !userInfo.OwnPermissions.IsPaused && !userInfo.OtherPermissions.IsPaused)
            {
                return true;
            }

            return false;
        }

        // check for individual pairs
        if (charaData.AccessType == CharaDataAccess.ClosePairs)
        {
            if (pairInfoUploader.TryGetValue(UserUID, out var userInfo) && userInfo.IsSynced && !userInfo.OwnPermissions.IsPaused && !userInfo.OtherPermissions.IsPaused
                && userInfo.IndividuallyPaired)
            {
                return true;
            }

            return false;
        }

        return false;
    }

    private async Task<CharaData> GetCharaDataById(string id, string methodName)
    {
        var splitid = id.Split(":", StringSplitOptions.None);
        if (splitid.Length != 2)
        {
            _logger.LogCallWarning(MareHubLogger.Args("INVALID", id));
            throw new InvalidOperationException($"Id {id} not in expected format");
        }

        var charaData = await DbContext.CharaData
                .Include(u => u.Files)
                .Include(u => u.FileSwaps)
                .Include(u => u.AllowedIndividiuals)
                .Include(u => u.Poses)
                .Include(u => u.Uploader)
                .AsSplitQuery()
                .SingleOrDefaultAsync(c => c.Id == splitid[1] && c.UploaderUID == splitid[0]).ConfigureAwait(false);

        if (charaData == null)
        {
            _logger.LogCallWarning(MareHubLogger.Args("NOT FOUND", id));
            throw new InvalidDataException($"No chara data with {id} found");
        }

        var groups = await DbContext.GroupPairs.Where(u => u.GroupUserUID == UserUID).Select(k => k.GroupGID).ToListAsync()
            .ConfigureAwait(false);

        if (!await CheckCharaDataAllowance(charaData, groups).ConfigureAwait(false))
        {
            _logger.LogCallWarning(MareHubLogger.Args("UNAUTHORIZED", id));
            throw new UnauthorizedAccessException($"User is not allowed to download {id}");
        }

        return charaData;
    }
}
