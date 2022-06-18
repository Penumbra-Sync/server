using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
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
        public Files(MareDbContext dbContext) : base(dbContext)
        {
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task SendFiles(List<FileReplacementDto> fileList)
        {
            var fileListHashes = fileList.Select(x => x.Hash).ToList();
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
                await Clients.Caller!.SendAsync("FileRequest", file);
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task<bool> IsUploadFinished()
        {
            var userUid = AuthenticatedUserId;
            return await DbContext.Files.AnyAsync(f => f.Uploader.UID == userUid && !f.Uploaded);
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task<bool> UploadFile(string hash, byte[] file)
        {
            var relatedFile = DbContext.Files.SingleOrDefault(f => f.Hash == hash);
            if (relatedFile == null) return false;
            var decodedFile = LZ4.LZ4Codec.Unwrap(file);
            using var sha1 = new SHA1CryptoServiceProvider();
            var computedHash = await sha1.ComputeHashAsync(new MemoryStream(decodedFile));
            var computedHashString = BitConverter.ToString(computedHash).Replace("-", "");
            if (hash != computedHashString)
            {
                DbContext.Remove(relatedFile);
                await DbContext.SaveChangesAsync();
                return false;
            }
            await File.WriteAllBytesAsync(@"G:\ServerTest\" + hash, file);
            relatedFile.Uploaded = true;
            relatedFile.LastAccessTime = DateTime.Now;
            await DbContext.SaveChangesAsync();
            return true;
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task<byte[]> DownloadFile(string hash)
        {
            var file = DbContext.Files.SingleOrDefault(f => f.Hash == hash);
            if (file == null) return Array.Empty<byte>();
            return await File.ReadAllBytesAsync(@"G:\ServerTest\" + hash);
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
