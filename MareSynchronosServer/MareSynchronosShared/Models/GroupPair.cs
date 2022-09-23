using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MareSynchronosShared.Models;

public class GroupPair
{
    public string GroupGID { get; set; }
    public Group Group { get; set; }
    public string GroupUserUID { get; set; }
    public User GroupUser { get; set; }
    public bool IsPaused { get; set; }
}
