using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MareSynchronosShared.Models;

public class GroupTempInvite
{
    public Group Group { get; set; }
    public string GroupGID { get; set; }
    [MaxLength(10)]
    public string Invite { get; set; }
    public DateTime ExpirationDate { get; set; }
}
