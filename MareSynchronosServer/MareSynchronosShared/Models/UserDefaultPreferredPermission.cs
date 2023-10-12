using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MareSynchronosShared.Models;

public class UserDefaultPreferredPermission
{
    [Key]
    [MaxLength(10)]
    [ForeignKey("User")]
    public string UserUID { get; set; }
    public User User { get; set; }

    public bool DisableIndividualAnimations { get; set; } = false;
    public bool DisableIndividualSounds { get; set; } = false;
    public bool DisableIndividualVFX { get; set; } = false;
    public bool DisableGroupAnimations { get; set; } = false;
    public bool DisableGroupSounds { get; set; } = false;
    public bool DisableGroupVFX { get; set; } = false;
    public bool IndividualIsSticky { get; set; } = false;
}

