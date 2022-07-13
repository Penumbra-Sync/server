using MareSynchronosServer.Models;
using Microsoft.EntityFrameworkCore;

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
        public DbSet<Auth> Auth { get; set; }


        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Auth>().ToTable("Auth");
            modelBuilder.Entity<User>().ToTable("Users");
            modelBuilder.Entity<User>().HasIndex(c => c.CharacterIdentification);
            modelBuilder.Entity<FileCache>().ToTable("FileCaches");
            modelBuilder.Entity<FileCache>().HasIndex(c => c.UploaderUID);
            modelBuilder.Entity<ClientPair>().ToTable("ClientPairs");
            modelBuilder.Entity<ClientPair>().HasKey(u => new { u.UserUID, u.OtherUserUID });
            modelBuilder.Entity<ClientPair>().HasIndex(c => c.UserUID);
            modelBuilder.Entity<ClientPair>().HasIndex(c => c.OtherUserUID);
            modelBuilder.Entity<ForbiddenUploadEntry>().ToTable("ForbiddenUploadEntries");
            modelBuilder.Entity<Banned>().ToTable("BannedUsers");
        }
    }
}
