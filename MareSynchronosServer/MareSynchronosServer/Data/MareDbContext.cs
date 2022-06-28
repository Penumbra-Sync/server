using System.Collections.Generic;
using MareSynchronos.API;
using MareSynchronosServer.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace MareSynchronosServer.Data
{
    public class MareDbContext : Microsoft.EntityFrameworkCore.DbContext
    {
        public MareDbContext(DbContextOptions<MareDbContext> options) : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<FileCache> Files { get; set; }
        public DbSet<ClientPair> ClientPairs { get; set; }
        public DbSet<CharacterData> CharacterData { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>().ToTable("Users");
            modelBuilder.Entity<FileCache>().ToTable("FileCaches");
            modelBuilder.Entity<ClientPair>().ToTable("ClientPairs");
            modelBuilder.Entity<CharacterData>()
                .Property(b => b.CharacterCache)
                .HasConversion(
                    v => JsonConvert.SerializeObject(v),
                    v => JsonConvert.DeserializeObject<CharacterCacheDto>(v));
            modelBuilder.Entity<CharacterData>()
                .ToTable("CharacterData")
                .HasKey(k => new { k.UserId, k.JobId });
        }
    }
}
