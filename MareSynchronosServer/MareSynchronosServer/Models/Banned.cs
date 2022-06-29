using System.ComponentModel.DataAnnotations;

namespace MareSynchronosServer.Models
{
    public class Banned
    {
        [Key]
        public string CharacterIdentification { get; set; }
    }
}
