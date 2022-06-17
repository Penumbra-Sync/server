using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MareSynchronosServer.Authentication;
using MareSynchronosServer.Data;
using MareSynchronosServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MareSynchronosServer.Hubs
{
    [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
    public class Files : Hub
    {
        private readonly MareDbContext _dbContext;

        public Files(MareDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task SendFiles(List<string> fileList)
        {
            var existingFiles = _dbContext.Files.Where(f => fileList.Contains(f.Hash)).ToList();
            foreach (var file in fileList.Where(f => existingFiles.All(e => e.Hash != f)))
            {
                var userId = Context.User!.Claims.SingleOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value ?? "Unknown";
                await _dbContext.Files.AddAsync(new FileCache()
                {
                    Hash = file,
                    LastAccessTime = DateTime.Now,
                    Uploaded = false,
                    Uploader = _dbContext.Users.Single(u => u.UID == userId)
                });
                await _dbContext.SaveChangesAsync();
                await Clients.Caller!.SendAsync("FileRequest", file);
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyAuthenticationHandler.AUTH_SCHEME)]
        public async Task<bool> UploadFile(string hash, byte[] file)
        {
            var relatedFile = _dbContext.Files.SingleOrDefault(f => f.Hash == hash);
            if (relatedFile == null) return false;
            var decodedFile = LZ4.LZ4Codec.Unwrap(file);
            using var sha1 = new SHA1CryptoServiceProvider();
            var computedHash = await sha1.ComputeHashAsync(new MemoryStream(decodedFile));
            var computedHashString = BitConverter.ToString(computedHash).Replace("-", "");
            if (hash != computedHashString)
            {
                return false;
            }
            await File.WriteAllBytesAsync(@"G:\ServerTest\" + hash, file);
            relatedFile.Uploaded = true;
            relatedFile.LastAccessTime = DateTime.Now;
            await _dbContext.SaveChangesAsync();
            return true;
        }

        public override Task OnDisconnectedAsync(Exception exception)
        {
            var userId = Context.User!.Claims.SingleOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
            var notUploadedFiles = _dbContext.Files.Where(f => !f.Uploaded && f.Uploader.UID == userId).ToList();
            _dbContext.RemoveRange(notUploadedFiles);
            _dbContext.SaveChanges();
            return base.OnDisconnectedAsync(exception);
        }
    }
}
