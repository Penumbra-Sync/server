using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace MareSynchronos.API
{
    public class CharacterCacheDto
    {
        public List<FileReplacementDto> FileReplacements { get; set; } = new();
        public string GlamourerData { get; set; }
        public string Hash { get; set; }
        public int JobId { get; set; }
    }

    public class FileReplacementDto
    {
        public string[] GamePaths { get; set; } = Array.Empty<string>();
        public string Hash { get; set; }
        public string ImcData { get; set; }
    }
}
