namespace MareSynchronosShared.Models;

public class GroupPairPreferredPermission
{
    public string GroupGID { get; set; }
    public Group Group { get; set; }
    public string UserUID { get; set; }
    public User User { get; set; }
    public bool IsPaused { get; set; }
    public bool DisableAnimations { get; set; }
    public bool DisableSounds { get; set; }
    public bool DisableVFX { get; set; }
}
