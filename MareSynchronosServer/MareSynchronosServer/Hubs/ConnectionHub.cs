using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Hubs
{
    public class ConnectionHub : Hub
    {
        private readonly ILogger<ConnectionHub> _logger;

        public ConnectionHub(ILogger<ConnectionHub> logger)
        {
            _logger = logger;
        }

        public string Heartbeat()
        {
            var userId = Context.User!.Claims.SingleOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
            _logger.LogInformation("Heartbeat from " + (userId ?? "Unknown user"));
            return userId ?? string.Empty;
        }
    }
}
