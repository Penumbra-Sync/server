using System.Collections.Generic;
using MareSynchronos.API;
using MareSynchronosServer.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace MareSynchronosServer.Data
{
    public class MareDbContext : DbContext
    {
        public MareDbContext(DbContextOptions<MareDbContext> options) : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<FileCache> Files { get; set; }
        public DbSet<ClientPair> ClientPairs { get; set; }
        public DbSet<ForbiddenUploadEntry> ForbiddenUploadEntries { get; set; }
        public DbSet<Banned> BannedUsers { get; set; }


        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>().ToTable("Users");
            modelBuilder.Entity<FileCache>().ToTable("FileCaches");
            modelBuilder.Entity<ClientPair>().ToTable("ClientPairs");
            modelBuilder.Entity<ClientPair>().HasKey(u => new { u.UserUID, u.OtherUserUID });
            modelBuilder.Entity<ClientPair>().HasIndex(c => c.UserUID);
            modelBuilder.Entity<ClientPair>().HasIndex(c => c.OtherUserUID);
            modelBuilder.Entity<ForbiddenUploadEntry>().ToTable("ForbiddenUploadEntries");
            modelBuilder.Entity<Banned>().ToTable("BannedUsers");
        }
    }
}
