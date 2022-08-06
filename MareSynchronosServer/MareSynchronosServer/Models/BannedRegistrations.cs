using System.ComponentModel.DataAnnotations;

namespace MareSynchronosServer.Models
{
    public class BannedRegistrations
    {
        [Key]
        public string DiscordIdOrLodestoneAuth { get; set; }
    }
}
