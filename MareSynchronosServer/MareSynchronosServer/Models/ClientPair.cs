using System.ComponentModel.DataAnnotations;

namespace MareSynchronosServer.Models
{
    public class ClientPair
    {
        public int Id { get; set; }
        public User User { get; set; }
        public User OtherUser { get; set; }
        public bool IsPaused { get; set; }
        [Timestamp]
        public byte[] Timestamp { get; set; }
    }
}
