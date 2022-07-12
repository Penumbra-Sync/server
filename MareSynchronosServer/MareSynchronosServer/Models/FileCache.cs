using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MareSynchronosServer.Models
{
    public class FileCache
    {
        [Key]
        [MaxLength(40)]
        public string Hash { get; set; }
        public User Uploader { get; set; }
        public bool Uploaded { get; set; }
        [Timestamp]
        public byte[] Timestamp { get; set; }
    }
}
