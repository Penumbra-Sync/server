using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MareSynchronos.API
{
    public class UploadFileDto
    {
        public string Hash { get; set; } = string.Empty;
        public bool IsForbidden { get; set; } = false;
        public string ForbiddenBy { get; set; } = string.Empty;
    }
}
