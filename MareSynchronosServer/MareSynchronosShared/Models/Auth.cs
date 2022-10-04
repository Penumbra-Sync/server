using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;

public class Auth
{
    [Key]
    [MaxLength(64)]
    public string HashedKey { get; set; }

    public string UserUID { get; set; }
    public User User { get; set; }
}
