using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosServer.Authentication;
using MareSynchronosServer.Data;
using MareSynchronosServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs
{
    public class Files : BaseHub
    {
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> UserUploads = new();
        public Files(MareDbContext dbContext) : base(dbContext)
        {
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task AbortUpload()
        {
            var userId = AuthenticatedUserId;
            var notUploadedFiles = DbContext.Files.Where(f => !f.Uploaded && f.Uploader.UID == userId).ToList();
            DbContext.RemoveRange(notUploadedFiles);
            await DbContext.SaveChangesAsync();
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task<List<string>> SendFiles(List<FileReplacementDto> fileList)
        {
            var fileListHashes = fileList.Select(x => x.Hash).ToList();
            List<string> filesToUpload = new List<string>();
            var existingFiles = DbContext.Files.Where(f => fileListHashes.Contains(f.Hash)).ToList();
            foreach (var file in fileListHashes.Where(f => existingFiles.All(e => e.Hash != f)))
            {
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
        public async Task UploadFile(string hash, ChannelReader<byte[]> stream)
        {
            var relatedFile = DbContext.Files.SingleOrDefault(f => f.Hash == hash);
            if (relatedFile == null) return;
            List<byte> uploadedFile = new();
            while (await stream.WaitToReadAsync())
            {
                while (stream.TryRead(out var byteChunk))
                {
                    uploadedFile.AddRange(byteChunk);
                }
            }
            Debug.WriteLine(DateTime.Now.ToString(CultureInfo.InvariantCulture) + ": File size of " + hash + ":" + uploadedFile.Count);
            try
            {
                var decodedFile = LZ4.LZ4Codec.Unwrap(uploadedFile.ToArray());
                using var sha1 = new SHA1CryptoServiceProvider();
                var computedHash = await sha1.ComputeHashAsync(new MemoryStream(decodedFile));
                var computedHashString = BitConverter.ToString(computedHash).Replace("-", "");
                if (hash != computedHashString)
                {
                    DbContext.Remove(relatedFile);
                    await DbContext.SaveChangesAsync();
                    return;
                }

                await File.WriteAllBytesAsync(@"G:\ServerTest\" + hash, uploadedFile.ToArray());
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
            return new FileInfo(@"G:\ServerTest\" + hash).Length;
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task<ChannelReader<byte[]>> DownloadFile(string hash)
        {
            var file = DbContext.Files.SingleOrDefault(f => f.Hash == hash);
            if (file == null) return null;
            var compressedFile = await File.ReadAllBytesAsync(@"G:\ServerTest\" + hash);
            var chunkSize = 1024 * 512; // 512kb
            var chunks = (int)Math.Ceiling(compressedFile.Length / (double)chunkSize);
            var channel = Channel.CreateBounded<byte[]>(chunkSize);
            _ = Task.Run(() =>
            {
                for (var i = 0; i < chunks; i++)
                {
                    channel.Writer.TryWrite(compressedFile.Skip(i * chunkSize).Take(chunkSize).ToArray());
                }

                channel.Writer.Complete();
            });

            return channel.Reader;
        }

        public override Task OnDisconnectedAsync(Exception exception)
        {
            Debug.WriteLine("Detected disconnect from " + AuthenticatedUserId);
            var userId = AuthenticatedUserId;
            var notUploadedFiles = DbContext.Files.Where(f => !f.Uploaded && f.Uploader.UID == userId).ToList();
            DbContext.RemoveRange(notUploadedFiles);
            DbContext.SaveChanges();
            return base.OnDisconnectedAsync(exception);
        }
    }
}
