using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MareSynchronosServer.Authentication;
using MareSynchronosServer.Data;
using MareSynchronosServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Hubs
{
    public class FilesHub : BaseHub<FilesHub>
    {
        private readonly IConfiguration _configuration;

        public FilesHub(ILogger<FilesHub> logger, MareDbContext context, IConfiguration configuration) : base(context, logger)
        {
            _configuration = configuration;
        }

        private string BasePath => _configuration["CacheDirectory"];
        
        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task AbortUpload()
        {
            Logger.LogInformation("User " + AuthenticatedUserId + " aborted upload");
            var userId = AuthenticatedUserId;
            var notUploadedFiles = DbContext.Files.Where(f => !f.Uploaded && f.Uploader.UID == userId).ToList();
            DbContext.RemoveRange(notUploadedFiles);
            await DbContext.SaveChangesAsync();
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task<List<string>> SendFiles(List<string> fileListHashes)
        {
            Logger.LogInformation("User " + AuthenticatedUserId + " sending files");
            List<string> filesToUpload = new List<string>();
            var existingFiles = DbContext.Files.Where(f => fileListHashes.Contains(f.Hash)).ToList();
            foreach (var file in fileListHashes.Where(f => existingFiles.All(e => e.Hash != f)))
            {
                Debug.WriteLine("File: " + file);
                var userId = AuthenticatedUserId;
                await DbContext.Files.AddAsync(new FileCache()
                {
                    Hash = file,
                    LastAccessTime = DateTime.Now,
                    Uploaded = false,
                    Uploader = DbContext.Users.Single(u => u.UID == userId)
                });
                await DbContext.SaveChangesAsync();
                filesToUpload.Add(file);
            }

            return filesToUpload;
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task<bool> IsUploadFinished()
        {
            var userUid = AuthenticatedUserId;
            return await DbContext.Files.AnyAsync(f => f.Uploader.UID == userUid && !f.Uploaded);
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task UploadFileStreamAsync(string hash, IAsyncEnumerable<byte[]> fileContent)
        {
            Logger.LogInformation("User " + AuthenticatedUserId + " uploading file: " + hash);

            var relatedFile = DbContext.Files.SingleOrDefault(f => f.Hash == hash && f.Uploader.UID == AuthenticatedUserId && f.Uploaded == false);
            if (relatedFile == null) return;
            var uploadedFile = new List<byte>();
            await foreach (var chunk in fileContent)
            {
                uploadedFile.AddRange(chunk);
            }

            Logger.LogInformation("User " + AuthenticatedUserId + " upload finished: " + hash + ", size: " + uploadedFile.Count);

            try
            {
                var decodedFile = LZ4.LZ4Codec.Unwrap(uploadedFile.ToArray());
                using var sha1 = SHA1.Create();
                var computedHash = await sha1.ComputeHashAsync(new MemoryStream(decodedFile));
                var computedHashString = BitConverter.ToString(computedHash).Replace("-", "");
                if (hash != computedHashString)
                {
                    DbContext.Remove(relatedFile);
                    await DbContext.SaveChangesAsync();
                    return;
                }

                await File.WriteAllBytesAsync(Path.Combine(BasePath, hash), uploadedFile.ToArray());
                relatedFile = DbContext.Files.Single(f => f.Hash == hash);
                relatedFile.Uploaded = true;
                relatedFile.LastAccessTime = DateTime.Now;
                await DbContext.SaveChangesAsync();
                return;
            }
            catch (Exception ex)
            {
                Debug.Write(ex.Message);
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task<long> GetFileSize(string hash)
        {
            var file = await DbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
            if (file == null) return -1;
            var fileInfo = new FileInfo(Path.Combine(BasePath, hash));
            if (fileInfo.Exists) return fileInfo.Length;
            DbContext.Files.Remove(file);
            await DbContext.SaveChangesAsync();
            return -1;

        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async IAsyncEnumerable<byte[]> DownloadFileAsync(string hash, [EnumeratorCancellation] CancellationToken ct)
        {
            Logger.LogInformation("User " + AuthenticatedUserId + " downloading file: " + hash);

            var file = DbContext.Files.SingleOrDefault(f => f.Hash == hash);
            if (file == null) yield break;
            file.LastAccessTime = DateTime.Now;
            DbContext.Update(file);
            await DbContext.SaveChangesAsync(ct);
            var chunkSize = 1024 * 512; // 512kb
            int readByteCount;
            var buffer = new byte[chunkSize];

            await using var fs = File.Open(Path.Combine(BasePath, hash), FileMode.Open, FileAccess.Read);
            while ((readByteCount = await fs.ReadAsync(buffer, 0, chunkSize, ct)) > 0)
            {
                await Task.Delay(10, ct);
                yield return readByteCount == chunkSize ? buffer.ToArray() : buffer.Take(readByteCount).ToArray();
            }

            Logger.LogInformation("User " + AuthenticatedUserId + " finished downloading file: " + hash);
        }
        
        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task DeleteAllFiles()
        {
            Logger.LogInformation("User " + AuthenticatedUserId + " deleted all their files");

            DbContext.CharacterData.RemoveRange(DbContext.CharacterData.Where(c => c.UserId == AuthenticatedUserId));
            await DbContext.SaveChangesAsync();
            var ownFiles = await DbContext.Files.Where(f => f.Uploaded && f.Uploader.UID == AuthenticatedUserId).ToListAsync();
            foreach (var file in ownFiles)
            {
                File.Delete(Path.Combine(BasePath,  file.Hash));
            }
            DbContext.Files.RemoveRange(ownFiles);
            await DbContext.SaveChangesAsync();
        }

        public override Task OnDisconnectedAsync(Exception exception)
        {
            var userId = AuthenticatedUserId;
            var notUploadedFiles = DbContext.Files.Where(f => !f.Uploaded && f.Uploader.UID == userId).ToList();
            DbContext.RemoveRange(notUploadedFiles);
            DbContext.SaveChanges();
            return base.OnDisconnectedAsync(exception);
        }
    }
}
