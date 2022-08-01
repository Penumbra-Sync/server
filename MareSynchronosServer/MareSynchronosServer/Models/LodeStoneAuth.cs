using System;
using System.ComponentModel.DataAnnotations;

namespace MareSynchronosServer.Models
{
    public class LodeStoneAuth
    {
        [Key]
        public ulong DiscordId { get; set; }
        public string HashedLodestoneId { get; set; }
        public string? LodestoneAuthString { get; set; }
        public User? User { get; set; }
        public DateTime? StartedAt { get; set; }
    }
}
