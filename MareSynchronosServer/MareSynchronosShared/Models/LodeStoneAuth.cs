using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;

public class LodeStoneAuth
{
    [Key]
    public ulong DiscordId { get; set; }
    [MaxLength(100)]
    public string HashedLodestoneId { get; set; }
    [MaxLength(100)]
    public string? LodestoneAuthString { get; set; }
    public User? User { get; set; }
    public DateTime? StartedAt { get; set; }
}
