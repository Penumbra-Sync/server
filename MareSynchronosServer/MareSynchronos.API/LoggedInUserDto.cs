using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MareSynchronos.API
{
    public class LoggedInUserDto
    {
        public bool IsAdmin { get; set; }
        public bool IsModerator { get; set; }
        public string UID { get; set; }
    }
}
