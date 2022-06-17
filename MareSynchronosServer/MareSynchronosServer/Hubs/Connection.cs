using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace MareSynchronosServer.Hubs
{
    public class Connection : Hub
    {
        public string Heartbeat()
        {
            var userId = Context.User!.Claims.SingleOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
            if (userId != null)
            {
                var user = Clients.User(userId);
            }
            return userId ?? string.Empty;
        }
    }
}
