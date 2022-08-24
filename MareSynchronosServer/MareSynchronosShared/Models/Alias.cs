using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;

public class Alias
{
    [Key]
    [MaxLength(10)]
    [Required]
    public string AliasUID { get; set; }
    public User User { get; set; }
    public string UserUID { get; set; }
}
