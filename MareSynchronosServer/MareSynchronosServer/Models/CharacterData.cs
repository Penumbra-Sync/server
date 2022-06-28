using System.Collections.Generic;
using MareSynchronos.API;

namespace MareSynchronosServer.Models
{
    public class CharacterData
    {
        public string UserId { get; set; }
        public int JobId { get; set; }
        public CharacterCacheDto CharacterCache { get; set; }
        public string Hash { get; set; }
    }
}
