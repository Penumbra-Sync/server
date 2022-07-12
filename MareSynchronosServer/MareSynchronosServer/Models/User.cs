using System;
using System.ComponentModel.DataAnnotations;

namespace MareSynchronosServer.Models
{
    public class User
    {
        [Key]
        [MaxLength(10)]
        public string UID { get; set; }
        public string SecretKey { get; set; }
        public string CharacterIdentification { get; set; }
        [Timestamp]
        public byte[] Timestamp { get; set; }

        public bool IsModerator { get; set; } = false;

        public bool IsAdmin { get; set; } = false;
    }
}
