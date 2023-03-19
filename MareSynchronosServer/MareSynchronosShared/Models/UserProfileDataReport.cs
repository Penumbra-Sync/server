using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MareSynchronosShared.Models;

public class UserProfileDataReport
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public DateTime ReportDate { get; set; }
    public User ReportedUser { get; set; }

    [ForeignKey(nameof(ReportedUser))]
    public string ReportedUserUID { get; set; }

    public User ReportingUser { get; set; }

    [ForeignKey(nameof(ReportingUser))]
    public string ReportingUserUID { get; set; }

    public string ReportReason { get; set; }
}