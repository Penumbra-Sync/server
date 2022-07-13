using System.ComponentModel.DataAnnotations;

namespace MareSynchronosServer.Models
{
    public class ForbiddenUploadEntry
    {
        [Key]
        [MaxLength(40)]
        public string Hash { get; set; }
        public string ForbiddenBy { get; set; }
        [Timestamp]
        public byte[] Timestamp { get; set; }
    }
}
