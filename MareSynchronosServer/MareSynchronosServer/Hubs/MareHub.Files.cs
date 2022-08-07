using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosServer.Authentication;
using MareSynchronosServer.Metrics;
using MareSynchronosServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Hubs
{
    public partial class MareHub
    {
        private string BasePath => _configuration["CacheDirectory"];

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendFileAbortUpload)]
        public async Task AbortUpload()
        {
            _logger.LogInformation("User " + AuthenticatedUserId + " aborted upload");
            var userId = AuthenticatedUserId;
            var notUploadedFiles = _dbContext.Files.Where(f => !f.Uploaded && f.Uploader.UID == userId).ToList();
            _dbContext.RemoveRange(notUploadedFiles);
            await _dbContext.SaveChangesAsync();
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendFileDeleteAllFiles)]
        public async Task DeleteAllFiles()
        {
            _logger.LogInformation("User " + AuthenticatedUserId + " deleted all their files");

            var ownFiles = await _dbContext.Files.Where(f => f.Uploaded && f.Uploader.UID == AuthenticatedUserId).ToListAsync();
            foreach (var file in ownFiles)
            {
                var fi = new FileInfo(Path.Combine(BasePath, file.Hash));
                if (fi.Exists)
                {
                    MareMetrics.FilesTotalSize.Dec(fi.Length);
                    MareMetrics.FilesTotal.Dec();
                    fi.Delete();
                }
            }
            _dbContext.Files.RemoveRange(ownFiles);
            await _dbContext.SaveChangesAsync();
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeFileGetFileSize)]
        public async Task<DownloadFileDto> GetFileSize(string hash)
        {
            var file = await _dbContext.Files.AsNoTracking().SingleOrDefaultAsync(f => f.Hash == hash);
            var forbidden = _dbContext.ForbiddenUploadEntries.AsNoTracking().
                SingleOrDefault(f => f.Hash == hash);
            var fileInfo = new FileInfo(Path.Combine(BasePath, hash));
            long fileSize = 0;
            try
            {
                fileSize = fileInfo.Length;
            }
            catch
            {
                // file doesn't exist anymore
            }

            var response = new DownloadFileDto
            {
                FileExists = fileInfo.Exists,
                ForbiddenBy = forbidden?.ForbiddenBy ?? string.Empty,
                IsForbidden = forbidden != null,
                Hash = hash,
                Size = fileSize
            };

            if (!fileInfo.Exists && file != null)
            {
                _dbContext.Files.Remove(file);
                await _dbContext.SaveChangesAsync();
            }

            return response;

        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeFileIsUploadFinished)]
        public async Task<bool> IsUploadFinished()
        {
            var userUid = AuthenticatedUserId;
            return await _dbContext.Files.AsNoTracking()
                .AnyAsync(f => f.Uploader.UID == userUid && !f.Uploaded);
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeFileSendFiles)]
        public async Task<List<UploadFileDto>> SendFiles(List<string> fileListHashes)
        {
            fileListHashes = fileListHashes.Where(f => !string.IsNullOrEmpty(f)).Distinct().ToList();
            _logger.LogInformation($"User {AuthenticatedUserId} sending {fileListHashes.Count} files");
            var forbiddenFiles = await _dbContext.ForbiddenUploadEntries.AsNoTracking().Where(f => fileListHashes.Contains(f.Hash)).ToListAsync();
            var filesToUpload = new List<UploadFileDto>();
            filesToUpload.AddRange(forbiddenFiles.Select(f => new UploadFileDto()
            {
                ForbiddenBy = f.ForbiddenBy,
                Hash = f.Hash,
                IsForbidden = true
            }));
            fileListHashes.RemoveAll(f => filesToUpload.Any(u => u.Hash == f));
            var existingFiles = await _dbContext.Files.AsNoTracking().Where(f => fileListHashes.Contains(f.Hash)).ToListAsync();
            foreach (var file in fileListHashes.Where(f => existingFiles.All(e => e.Hash != f) && filesToUpload.All(u => u.Hash != f)))
            {
                _logger.LogInformation("User " + AuthenticatedUserId + " needs upload: " + file);
                var userId = AuthenticatedUserId;
                await _dbContext.Files.AddAsync(new FileCache()
                {
                    Hash = file,
                    Uploaded = false,
                    Uploader = _dbContext.Users.Single(u => u.UID == userId)
                });
                await _dbContext.SaveChangesAsync();
                filesToUpload.Add(new UploadFileDto
                {
                    Hash = file
                });
            }

            return filesToUpload;
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendFileUploadFileStreamAsync)]
        public async Task UploadFileStreamAsync(string hash, IAsyncEnumerable<byte[]> fileContent)
        {
            _logger.LogInformation("User " + AuthenticatedUserId + " uploading file: " + hash);

            var relatedFile = _dbContext.Files.SingleOrDefault(f => f.Hash == hash && f.Uploader.UID == AuthenticatedUserId && f.Uploaded == false);
            if (relatedFile == null) return;
            var forbiddenFile = _dbContext.ForbiddenUploadEntries.SingleOrDefault(f => f.Hash == hash);
            if (forbiddenFile != null) return;
            var uploadedFile = new List<byte>();
            try
            {
                await foreach (var chunk in fileContent)
                {
                    uploadedFile.AddRange(chunk);
                }
            }
            catch
            {
                try
                {
                    _dbContext.Files.Remove(relatedFile);
                    await _dbContext.SaveChangesAsync();
                }
                catch
                {
                    // already removed
                }

                return;
            }

            _logger.LogInformation("User " + AuthenticatedUserId + " upload finished: " + hash + ", size: " + uploadedFile.Count);

            try
            {
                var decodedFile = LZ4.LZ4Codec.Unwrap(uploadedFile.ToArray());
                using var sha1 = SHA1.Create();
                using var ms = new MemoryStream(decodedFile);
                var computedHash = await sha1.ComputeHashAsync(ms);
                var computedHashString = BitConverter.ToString(computedHash).Replace("-", "");
                if (hash != computedHashString)
                {
                    _logger.LogWarning($"Computed file hash was not expected file hash. Computed: {computedHashString}, Expected {hash}");
                    _dbContext.Remove(relatedFile);
                    await _dbContext.SaveChangesAsync();

                    return;
                }

                await File.WriteAllBytesAsync(Path.Combine(BasePath, hash), uploadedFile.ToArray());
                relatedFile = _dbContext.Files.Single(f => f.Hash == hash);
                relatedFile.Uploaded = true;

                MareMetrics.FilesTotal.Inc();
                MareMetrics.FilesTotalSize.Inc(uploadedFile.Count);

                await _dbContext.SaveChangesAsync();
                _logger.LogInformation("File " + hash + " added to DB");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Upload failed");
                _dbContext.Remove(relatedFile);
                await _dbContext.SaveChangesAsync();
            }

        }
    }
}
