using System.ComponentModel.DataAnnotations;

namespace MareSynchronosServer.Models
{
    public class ForbiddenUploadEntry
    {
        [Key]
        public string Hash { get; set; }
        public string ForbiddenBy { get; set; }
    }
}
