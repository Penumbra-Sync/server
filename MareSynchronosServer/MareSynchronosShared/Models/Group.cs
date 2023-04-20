using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;

public class Group
{
    [Key]
    [MaxLength(20)]
    public string GID { get; set; }
    public string OwnerUID { get; set; }
    public User Owner { get; set; }
    [MaxLength(50)]
    public string Alias { get; set; }
    public bool InvitesEnabled { get; set; }
    public string HashedPassword { get; set; }
    public bool DisableSounds { get; set; }
    public bool DisableAnimations { get; set; }
    public bool DisableVFX { get; set; }
}
