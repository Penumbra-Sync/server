using System.ComponentModel.DataAnnotations;

namespace MareSynchronosServer.Models
{
    public class ClientPair
    {
        public int Id { get; set; }
        public User User { get; set; }
        public User OtherUser { get; set; }
        public bool IsPaused { get; set; }
        public bool AllowReceivingMessages { get; set; } = false;
        [Timestamp]
        public byte[] Timestamp { get; set; }
    }
}
