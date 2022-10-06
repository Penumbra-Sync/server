using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MareSynchronosShared.Models;

public class GroupBan
{
    public Group Group { get; set; }
    public string GroupGID { get; set; }
    public User BannedUser { get; set; }
    public string BannedUserUID { get; set; }
    public User BannedBy { get; set; }
    public string BannedByUID { get; set; }
    public DateTime BannedOn { get; set; }
    public string BannedReason { get; set; }
}
