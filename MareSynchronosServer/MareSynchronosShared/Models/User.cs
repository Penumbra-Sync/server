using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;

public class User
{
    [Key]
    [MaxLength(10)]
    public string UID { get; set; }
    [Timestamp]
    public byte[] Timestamp { get; set; }

    public bool IsModerator { get; set; } = false;

    public bool IsAdmin { get; set; } = false;

    public DateTime LastLoggedIn { get; set; }
    [MaxLength(15)]
    public string Alias { get; set; }
}
