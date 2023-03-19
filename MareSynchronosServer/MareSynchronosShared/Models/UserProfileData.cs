using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MareSynchronosShared.Models;

public class UserProfileData
{
    public string Base64ProfileImage { get; set; }
    public bool FlaggedForReport { get; set; }
    public bool IsNSFW { get; set; }
    public bool ProfileDisabled { get; set; }
    public User User { get; set; }

    public string UserDescription { get; set; }

    [Required]
    [Key]
    [ForeignKey(nameof(User))]
    public string UserUID { get; set; }
}