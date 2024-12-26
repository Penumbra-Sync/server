﻿using MareSynchronos.API.Dto.CharaData;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Models;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    [Authorize(Policy = "Identified")]
    public async Task<CharaDataFullDto?> CharaDataCreate()
    {
        _logger.LogCallInfo();

        int uploadCount = DbContext.CharaData.Count(c => c.UploaderUID == UserUID);
        User user = DbContext.Users.Single(u => u.UID == UserUID);
        int maximumUploads = string.IsNullOrEmpty(user.Alias) ? 10 : 30;
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
            ExpiryDate = null,
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
        var existingData = DbContext.CharaData.SingleOrDefaultAsync(u => u.Id == id && u.UploaderUID == UserUID);
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
        var splitid = id.Split(":", StringSplitOptions.None);
        if (splitid.Length != 2)
            return null;

        var charaData = await DbContext.CharaData.SingleOrDefaultAsync(c => c.Id == splitid[1] && c.UploaderUID == splitid[0]).ConfigureAwait(false);
        if (charaData == null)
            return null;

        if (!await CheckCharaDataAllowance(charaData).ConfigureAwait(false))
            return null;

        if (!string.Equals(charaData.UploaderUID, UserUID, StringComparison.Ordinal))
        {
            charaData.DownloadCount++;
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        return GetCharaDataDownloadDto(charaData);
    }

    [Authorize(Policy = "Identified")]
    public async Task<CharaDataMetaInfoDto?> CharaDataGetMetainfo(string id)
    {
        var splitid = id.Split(":", StringSplitOptions.None);
        if (splitid.Length != 2)
            return null;

        var charaData = await DbContext.CharaData.SingleOrDefaultAsync(c => c.Id == splitid[1] && c.UploaderUID == splitid[0]).ConfigureAwait(false);
        if (charaData == null)
            return null;

        if (!await CheckCharaDataAllowance(charaData).ConfigureAwait(false))
            return null;

        _logger.LogCallInfo(MareHubLogger.Args("SUCCESS", id));

        return GetCharaDataMetaInfoDto(charaData);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<CharaDataMetaInfoDto>> CharaDataGetShared()
    {
        var allPairs = await GetAllPairInfo(UserUID).ConfigureAwait(false);
        List<CharaData> sharedCharaData = [];
        foreach (var pair in allPairs.Where(p => !p.Value.OwnPermissions.IsPaused && !p.Value.OtherPermissions.IsPaused))
        {
            var allSharedDataByPair = await DbContext.CharaData.Where(p => p.ShareType == CharaDataShare.Shared && p.UploaderUID == pair.Key)
                .ToListAsync()
                .ConfigureAwait(false);

            foreach (var charaData in allSharedDataByPair)
            {
                if (await CheckCharaDataAllowance(charaData).ConfigureAwait(false))
                {
                    sharedCharaData.Add(charaData);
                }
            }
        }

        _logger.LogCallInfo(MareHubLogger.Args("SUCCESS", sharedCharaData.Count));

        return [.. sharedCharaData.Select(GetCharaDataMetaInfoDto)];
    }

    [Authorize(Policy = "Identified")]
    public async Task<CharaDataFullDto?> CharaDataUpdate(CharaDataUpdateDto updateDto)
    {
        var charaData = await DbContext.CharaData.SingleOrDefaultAsync(u => u.Id == updateDto.Id && u.UploaderUID == UserUID).ConfigureAwait(false);
        if (charaData == null)
            return null;

        bool anyChanges = false;

        if (updateDto.Description != null)
        {
            charaData.Description = updateDto.Description;
            anyChanges = true;
        }

        if (updateDto.ExpiryDate != null || (updateDto.ExpiryDate == null && charaData.ExpiryDate != null))
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
            var individuals = charaData.AllowedIndividiuals.ToList();
            charaData.AllowedIndividiuals.Clear();
            DbContext.RemoveRange(individuals);
            foreach (var user in updateDto.AllowedUsers)
            {
                charaData.AllowedIndividiuals.Add(new CharaDataAllowance()
                {
                    AllowedUserUID = user,
                    Parent = charaData
                });
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
                foreach (var path in file.Value)
                {
                    charaData.Files.Add(new CharaDataFile()
                    {
                        FileCacheHash = file.Key,
                        GamePath = path,
                        Parent = charaData
                    });
                }

                charaData.OriginalFiles.Add(new CharaDataOriginalFile()
                {
                    Hash = file.Key,
                    Parent = charaData
                });
            }
        }

        if (anyChanges)
        {
            _logger.LogCallInfo(MareHubLogger.Args("SUCCESS", anyChanges));
            charaData.UpdatedDate = DateTime.UtcNow;
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        return GetCharaDataFullDto(charaData);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<CharaDataFullDto>> CharaDataGetOwn()
    {
        var ownCharaData = await DbContext.CharaData.Where(c => c.UploaderUID == UserUID).ToListAsync().ConfigureAwait(false);
        _logger.LogCallInfo(MareHubLogger.Args("SUCCESS"));
        return [.. ownCharaData.Select(GetCharaDataFullDto)];
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
        return new CharaDataDownloadDto(charaData.Id, charaData.UploaderUID)
        {
            CustomizeData = charaData.CustomizeData,
            Description = charaData.Description,
            FileGamePaths = charaData.Files.GroupBy(f => f.FileCacheHash, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Select(d => d.GamePath).ToList(), StringComparer.Ordinal),
            GlamourerData = charaData.GlamourerData,
        };
    }

    private static CharaDataFullDto GetCharaDataFullDto(CharaData charaData)
    {
        return new CharaDataFullDto(charaData.Id, charaData.UploaderUID)
        {
            AccessType = GetAccessTypeDto(charaData.AccessType),
            ShareType = GetShareTypeDto(charaData.ShareType),
            AllowedUsers = [.. charaData.AllowedIndividiuals.Select(u => u.AllowedUserUID)],
            CustomizeData = charaData.CustomizeData,
            Description = charaData.Description,
            ExpectedHashes = [.. charaData.OriginalFiles.Select(f => f.Hash)],
            ExpiryDate = charaData.ExpiryDate,
            FileGamePaths = charaData.Files.GroupBy(f => f.FileCacheHash, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Select(d => d.GamePath).ToList(), StringComparer.Ordinal),
            GlamourerData = charaData.GlamourerData,
        };
    }

    private static CharaDataMetaInfoDto GetCharaDataMetaInfoDto(CharaData charaData)
    {
        var allOrigHashes = charaData.OriginalFiles.Select(k => k.Hash).ToList();
        var allFileHashes = charaData.Files.Select(f => f.FileCacheHash).ToList();
        var allHashesPresent = allOrigHashes.TrueForAll(h => allFileHashes.Contains(h, StringComparer.Ordinal));
        var canBeDownloaded = allHashesPresent |= !string.IsNullOrEmpty(charaData.GlamourerData);
        return new CharaDataMetaInfoDto(charaData.Id, charaData.UploaderUID)
        {
            CanBeDownloaded = canBeDownloaded,
            Description = charaData.Description,
            UpdatedDate = charaData.UpdatedDate
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

    private async Task<bool> CheckCharaDataAllowance(CharaData charaData)
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
}
