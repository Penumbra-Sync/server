using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;

public class ForbiddenUploadEntry
{
    [Key]
    [MaxLength(40)]
    public string Hash { get; set; }
    [MaxLength(100)]
    public string ForbiddenBy { get; set; }
    [Timestamp]
    public byte[] Timestamp { get; set; }
}
