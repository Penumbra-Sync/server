using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;

public class GroupTempInvite
{
    public Group Group { get; set; }
    public string GroupGID { get; set; }
    [MaxLength(64)]
    public string Invite { get; set; }
    public DateTime ExpirationDate { get; set; }
}
