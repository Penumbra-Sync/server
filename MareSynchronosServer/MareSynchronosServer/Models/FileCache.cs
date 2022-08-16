using System.ComponentModel.DataAnnotations;

namespace MareSynchronosServer.Models
{
    public class FileCache
    {
        [Key]
        [MaxLength(40)]
        public string Hash { get; set; }
        [MaxLength(10)]
        public string UploaderUID { get; set; }
        public User Uploader { get; set; }
        public bool Uploaded { get; set; }
        [Timestamp]
        public byte[] Timestamp { get; set; }
    }
}
