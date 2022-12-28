using System.Security.Claims;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using MareSynchronos.API;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Models;
using MareSynchronosShared.Protos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    private static readonly SemaphoreSlim _uploadSemaphore = new(20);

    [Authorize(Policy = "Identified")]
    public async Task FilesAbortUpload()
    {
        _logger.LogCallInfo();
        var userId = AuthenticatedUserId;
        var notUploadedFiles = _dbContext.Files.Where(f => !f.Uploaded && f.Uploader.UID == userId).ToList();
        _dbContext.RemoveRange(notUploadedFiles);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task FilesDeleteAll()
    {
        _logger.LogCallInfo();

        var ownFiles = await _dbContext.Files.Where(f => f.Uploaded && f.Uploader.UID == AuthenticatedUserId).ToListAsync().ConfigureAwait(false);
        var request = new DeleteFilesRequest();
        request.Hash.AddRange(ownFiles.Select(f => f.Hash));
        Metadata headers = new Metadata()
            {
                { "Authorization", Context.User!.Claims.SingleOrDefault(c => string.Equals(c.Type, ClaimTypes.Authentication, StringComparison.Ordinal))?.Value }
            };
        _ = await _fileServiceClient.DeleteFilesAsync(request, headers).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<DownloadFileDto>> FilesGetSizes(List<string> hashes)
    {
        _logger.LogCallInfo(MareHubLogger.Args(hashes.Count.ToString()));

        var allFiles = await _dbContext.Files.Where(f => hashes.Contains(f.Hash)).ToListAsync().ConfigureAwait(false);
        var forbiddenFiles = await _dbContext.ForbiddenUploadEntries.
            Where(f => hashes.Contains(f.Hash)).ToListAsync().ConfigureAwait(false);
        List<DownloadFileDto> response = new();

        var cacheFile = await _dbContext.Files.AsNoTracking().Where(f => hashes.Contains(f.Hash)).AsNoTracking().Select(k => new { k.Hash, k.Size }).AsNoTracking().ToListAsync().ConfigureAwait(false);

        foreach (var file in cacheFile)
        {
            var forbiddenFile = forbiddenFiles.SingleOrDefault(f => string.Equals(f.Hash, file.Hash, StringComparison.OrdinalIgnoreCase));
            var downloadFile = allFiles.SingleOrDefault(f => string.Equals(f.Hash, file.Hash, StringComparison.OrdinalIgnoreCase));

            response.Add(new DownloadFileDto
            {
                FileExists = file.Size > 0,
                ForbiddenBy = forbiddenFile?.ForbiddenBy ?? string.Empty,
                IsForbidden = forbiddenFile != null,
                Hash = file.Hash,
                Size = file.Size,
                Url = new Uri(_cdnFullUri, file.Hash.ToUpperInvariant()).ToString()
            });
        }

        return response;
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> FilesIsUploadFinished()
    {
        _logger.LogCallInfo();
        var userUid = AuthenticatedUserId;
        return await _dbContext.Files.AsNoTracking()
            .AnyAsync(f => f.Uploader.UID == userUid && !f.Uploaded).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<UploadFileDto>> FilesSend(List<string> fileListHashes)
    {
        var userSentHashes = new HashSet<string>(fileListHashes.Distinct(StringComparer.Ordinal), StringComparer.Ordinal);
        _logger.LogCallInfo(MareHubLogger.Args(userSentHashes.Count.ToString()));
        var notCoveredFiles = new Dictionary<string, UploadFileDto>(StringComparer.Ordinal);
        var forbiddenFiles = await _dbContext.ForbiddenUploadEntries.AsNoTracking().Where(f => userSentHashes.Contains(f.Hash)).AsNoTracking().ToDictionaryAsync(f => f.Hash, f => f).ConfigureAwait(false);
        var existingFiles = await _dbContext.Files.AsNoTracking().Where(f => userSentHashes.Contains(f.Hash)).AsNoTracking().ToDictionaryAsync(f => f.Hash, f => f).ConfigureAwait(false);
        var uploader = await _dbContext.Users.SingleAsync(u => u.UID == AuthenticatedUserId).ConfigureAwait(false);

        List<FileCache> fileCachesToUpload = new();
        foreach (var file in userSentHashes)
        {
            // Skip empty file hashes, duplicate file hashes, forbidden file hashes and existing file hashes
            if (string.IsNullOrEmpty(file)) { continue; }
            if (notCoveredFiles.ContainsKey(file)) { continue; }
            if (forbiddenFiles.ContainsKey(file))
            {
                notCoveredFiles[file] = new UploadFileDto()
                {
                    ForbiddenBy = forbiddenFiles[file].ForbiddenBy,
                    Hash = file,
                    IsForbidden = true
                };

                continue;
            }
            if (existingFiles.ContainsKey(file)) { continue; }

            _logger.LogCallInfo(MareHubLogger.Args(file, "Missing"));

            var userId = AuthenticatedUserId;
            fileCachesToUpload.Add(new FileCache()
            {
                Hash = file,
                Uploaded = false,
                Uploader = uploader
            });

            notCoveredFiles[file] = new UploadFileDto()
            {
                Hash = file,
            };
        }
        //Save bulk
        await _dbContext.Files.AddRangeAsync(fileCachesToUpload).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        return notCoveredFiles.Values.ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task FilesUploadStreamAsync(string hash, IAsyncEnumerable<byte[]> fileContent)
    {
        _logger.LogCallInfo(MareHubLogger.Args(hash));

        await _uploadSemaphore.WaitAsync(Context.ConnectionAborted).ConfigureAwait(false);

        var relatedFile = _dbContext.Files.SingleOrDefault(f => f.Hash == hash && f.Uploader.UID == AuthenticatedUserId && !f.Uploaded);
        if (relatedFile == null)
        {
            _uploadSemaphore.Release();
            return;
        }
        var forbiddenFile = _dbContext.ForbiddenUploadEntries.SingleOrDefault(f => f.Hash == hash);
        if (forbiddenFile != null)
        {
            _uploadSemaphore.Release();
            return;
        }

        var tempFileName = Path.GetTempFileName();
        using var fileStream = new FileStream(tempFileName, FileMode.OpenOrCreate);
        long length = 0;
        try
        {
            await foreach (var chunk in fileContent.ConfigureAwait(false))
            {
                length += chunk.Length;
                await fileStream.WriteAsync(chunk).ConfigureAwait(false);
            }

            await fileStream.FlushAsync().ConfigureAwait(false);
            await fileStream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await fileStream.FlushAsync().ConfigureAwait(false);
                await fileStream.DisposeAsync().ConfigureAwait(false);
                _dbContext.Files.Remove(relatedFile);
                await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            }
            catch
            {
                // already removed
            }
            finally
            {
                File.Delete(tempFileName);
            }

            _uploadSemaphore.Release();
            return;
        }

        _logger.LogCallInfo(MareHubLogger.Args(hash, "Uploaded"));

        try
        {
            var decodedFile = LZ4.LZ4Codec.Unwrap(await File.ReadAllBytesAsync(tempFileName).ConfigureAwait(false));
            using var sha1 = SHA1.Create();
            using var ms = new MemoryStream(decodedFile);
            var computedHash = await sha1.ComputeHashAsync(ms).ConfigureAwait(false);
            var computedHashString = BitConverter.ToString(computedHash).Replace("-", "", StringComparison.Ordinal);
            if (!string.Equals(hash, computedHashString, StringComparison.Ordinal))
            {
                _logger.LogCallWarning(MareHubLogger.Args(hash, "Invalid", computedHashString));
                _dbContext.Remove(relatedFile);
                await _dbContext.SaveChangesAsync().ConfigureAwait(false);

                _uploadSemaphore.Release();
                return;
            }

            Metadata headers = new Metadata()
            {
                { "Authorization", Context.User!.Claims.SingleOrDefault(c => string.Equals(c.Type, ClaimTypes.Authentication, StringComparison.Ordinal))?.Value }
            };
            var streamingCall = _fileServiceClient.UploadFile(headers);
            using var tempFileStream = new FileStream(tempFileName, FileMode.Open, FileAccess.Read);
            int size = 1024 * 1024;
            byte[] data = new byte[size];
            int readBytes;
            while ((readBytes = tempFileStream.Read(data, 0, size)) > 0)
            {
                await streamingCall.RequestStream.WriteAsync(new UploadFileRequest()
                {
                    FileData = ByteString.CopyFrom(data, 0, readBytes),
                    Hash = computedHashString,
                    Uploader = AuthenticatedUserId
                }).ConfigureAwait(false);
            }
            await streamingCall.RequestStream.CompleteAsync().ConfigureAwait(false);
            tempFileStream.Close();
            await tempFileStream.DisposeAsync().ConfigureAwait(false);

            _logger.LogCallInfo(MareHubLogger.Args(hash, "Pushed"));
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(MareHubLogger.Args("Failed", hash, ex.Message));
            _dbContext.Remove(relatedFile);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        finally
        {
            _uploadSemaphore.Release();
            File.Delete(tempFileName);
        }
    }
}
