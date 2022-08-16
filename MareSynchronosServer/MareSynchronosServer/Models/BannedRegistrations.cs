using System.ComponentModel.DataAnnotations;

namespace MareSynchronosServer.Models
{
    public class BannedRegistrations
    {
        [Key]
        [MaxLength(100)]
        public string DiscordIdOrLodestoneAuth { get; set; }
    }
}
