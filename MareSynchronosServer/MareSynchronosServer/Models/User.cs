using System;
using System.ComponentModel.DataAnnotations;

namespace MareSynchronosServer.Models
{
    public class User
    {
        [Key]
        public string UID { get; set; }
        public string SecretKey { get; set; }
        public string CharacterIdentification { get; set; }
        [Timestamp]
        public byte[] Timestamp { get; set; }
    }
}
