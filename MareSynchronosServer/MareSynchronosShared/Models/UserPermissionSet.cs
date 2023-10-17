using System.Diagnostics.CodeAnalysis;

namespace MareSynchronosShared.Models;

public class UserPermissionSet
{
    [NotNull]
    public string UserUID { get; set; }
    public User User { get; set; }
    [NotNull]
    public string OtherUserUID { get; set; }
    public User OtherUser { get; set; }
    public bool Sticky { get; set; } = false;
    public bool IsPaused { get; set; } = false;
    public bool DisableAnimations { get; set; } = false;
    public bool DisableVFX { get; set; } = false;
    public bool DisableSounds { get; set; } = false;
}
