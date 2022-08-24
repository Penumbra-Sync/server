using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using MareSynchronos.API;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Models;
using MareSynchronosShared.Protos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Hubs
{
    public partial class MareHub
    {
        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendFileAbortUpload)]
        public async Task AbortUpload()
        {
            _logger.LogInformation("User {AuthenticatedUserId} aborted upload", AuthenticatedUserId);
            var userId = AuthenticatedUserId;
            var notUploadedFiles = _dbContext.Files.Where(f => !f.Uploaded && f.Uploader.UID == userId).ToList();
            _dbContext.RemoveRange(notUploadedFiles);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendFileDeleteAllFiles)]
        public async Task DeleteAllFiles()
        {
            _logger.LogInformation("User {AuthenticatedUserId} deleted all their files", AuthenticatedUserId);

            var ownFiles = await _dbContext.Files.Where(f => f.Uploaded && f.Uploader.UID == AuthenticatedUserId).ToListAsync().ConfigureAwait(false);
            var request = new DeleteFilesRequest();
            request.Hash.AddRange(ownFiles.Select(f => f.Hash));
            Metadata headers = new Metadata()
                {
                    { "Authorization", Context.User!.Claims.SingleOrDefault(c => c.Type == ClaimTypes.Authentication)?.Value }
                };
            _ = await _fileServiceClient.DeleteFilesAsync(request, headers).ConfigureAwait(false);
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeGetFilesSizes)]
        public async Task<List<DownloadFileDto>> GetFilesSizes(List<string> hashes)
        {
            var allFiles = await _dbContext.Files.Where(f => hashes.Contains(f.Hash)).ToListAsync().ConfigureAwait(false);
            var forbiddenFiles = await _dbContext.ForbiddenUploadEntries.
                Where(f => hashes.Contains(f.Hash)).ToListAsync().ConfigureAwait(false);
            List<DownloadFileDto> response = new();

            FileSizeRequest request = new FileSizeRequest();
            request.Hash.AddRange(hashes);
            Metadata headers = new Metadata()
                {
                    { "Authorization", Context.User!.Claims.SingleOrDefault(c => c.Type == ClaimTypes.Authentication)?.Value }
                };
            var grpcResponse = await _fileServiceClient.GetFileSizesAsync(request, headers).ConfigureAwait(false);

            foreach (var hash in grpcResponse.HashToFileSize)
            {
                var forbiddenFile = forbiddenFiles.SingleOrDefault(f => f.Hash == hash.Key);
                var downloadFile = allFiles.SingleOrDefault(f => f.Hash == hash.Key);

                response.Add(new DownloadFileDto
                {
                    FileExists = hash.Value > 0,
                    ForbiddenBy = forbiddenFile?.ForbiddenBy ?? string.Empty,
                    IsForbidden = forbiddenFile != null,
                    Hash = hash.Key,
                    Size = hash.Value,
                    Url = new Uri(cdnFullUri, hash.Key.ToUpperInvariant()).ToString()
                });
            }

            return response;
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeFileIsUploadFinished)]
        public async Task<bool> IsUploadFinished()
        {
            var userUid = AuthenticatedUserId;
            return await _dbContext.Files.AsNoTracking()
                .AnyAsync(f => f.Uploader.UID == userUid && !f.Uploaded).ConfigureAwait(false);
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeFileSendFiles)]
        public async Task<List<UploadFileDto>> SendFiles(List<string> fileListHashes)
        {
            var userSentHashes = new HashSet<string>(fileListHashes.Distinct());
            _logger.LogInformation("User {AuthenticatedUserId} sending files: {count}", AuthenticatedUserId, userSentHashes.Count);
            var notCoveredFiles = new Dictionary<string, UploadFileDto>();
            var forbiddenFiles = await _dbContext.ForbiddenUploadEntries.AsNoTracking().Where(f => userSentHashes.Contains(f.Hash)).ToDictionaryAsync(f => f.Hash, f => f).ConfigureAwait(false);
            var existingFiles = await _dbContext.Files.AsNoTracking().Where(f => userSentHashes.Contains(f.Hash)).ToDictionaryAsync(f => f.Hash, f => f).ConfigureAwait(false);
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

                _logger.LogInformation("User {AuthenticatedUserId}  needs upload: {file}", AuthenticatedUserId, file);
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

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendFileUploadFileStreamAsync)]
        public async Task UploadFileStreamAsync(string hash, IAsyncEnumerable<byte[]> fileContent)
        {
            _logger.LogInformation("User {AuthenticatedUserId} uploading file: {hash}", AuthenticatedUserId, hash);

            var relatedFile = _dbContext.Files.SingleOrDefault(f => f.Hash == hash && f.Uploader.UID == AuthenticatedUserId && f.Uploaded == false);
            if (relatedFile == null) return;
            var forbiddenFile = _dbContext.ForbiddenUploadEntries.SingleOrDefault(f => f.Hash == hash);
            if (forbiddenFile != null) return;

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

                return;
            }

            _logger.LogInformation("User {AuthenticatedUserId} upload finished: {hash}, size: {length}", AuthenticatedUserId, hash, length);

            try
            {
                var decodedFile = LZ4.LZ4Codec.Unwrap(await File.ReadAllBytesAsync(tempFileName).ConfigureAwait(false));
                using var sha1 = SHA1.Create();
                using var ms = new MemoryStream(decodedFile);
                var computedHash = await sha1.ComputeHashAsync(ms).ConfigureAwait(false);
                var computedHashString = BitConverter.ToString(computedHash).Replace("-", "");
                if (hash != computedHashString)
                {
                    _logger.LogWarning("Computed file hash was not expected file hash. Computed: {computedHashString}, Expected {hash}", computedHashString, hash);
                    _dbContext.Remove(relatedFile);
                    await _dbContext.SaveChangesAsync().ConfigureAwait(false);

                    return;
                }

                UploadFileRequest req = new();
                req.FileData = ByteString.CopyFrom(await File.ReadAllBytesAsync(tempFileName).ConfigureAwait(false));
                File.Delete(tempFileName);
                req.Hash = computedHashString;
                req.Uploader = AuthenticatedUserId;
                Metadata headers = new Metadata()
                {
                    { "Authorization", Context.User!.Claims.SingleOrDefault(c => c.Type == ClaimTypes.Authentication)?.Value }
                };
                _ = await _fileServiceClient.UploadFileAsync(req, headers).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Upload failed");
                _dbContext.Remove(relatedFile);
                await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            }
        }
    }
}
