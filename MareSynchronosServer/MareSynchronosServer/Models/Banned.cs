using System.ComponentModel.DataAnnotations;

namespace MareSynchronosServer.Models
{
    public class Banned
    {
        [Key]
        public string CharacterIdentification { get; set; }
        public string Reason { get; set; }
        [Timestamp]
        public byte[] Timestamp { get; set; }
    }
}
