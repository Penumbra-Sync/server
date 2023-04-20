using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;

public class ClientPair
{
    [MaxLength(10)]
    public string UserUID { get; set; }
    public User User { get; set; }
    [MaxLength(10)]
    public string OtherUserUID { get; set; }
    public User OtherUser { get; set; }
    public bool IsPaused { get; set; }
    public bool AllowReceivingMessages { get; set; } = false;
    [Timestamp]
    public byte[] Timestamp { get; set; }
    public bool DisableSounds { get; set; } = false;
    public bool DisableAnimations { get; set; } = false;
    public bool DisableVFX { get; set; } = false;
}
