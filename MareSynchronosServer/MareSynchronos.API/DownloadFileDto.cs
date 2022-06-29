using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MareSynchronos.API
{
    public class DownloadFileDto
    {
        public bool FileExists { get; set; } = true;
        public string Hash { get; set; } = string.Empty;
        public long Size { get; set; } = 0;
        public bool IsForbidden { get; set; } = false;
        public string ForbiddenBy { get; set; } = string.Empty;
    }
}
