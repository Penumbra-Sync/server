using System.Linq;
using System.Runtime.ConstrainedExecution;
using System.Security.Claims;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Hubs
{
    public class ConnectionHub : BaseHub<ConnectionHub>
    {
        private const int ServerVersion = 1;
        public ConnectionHub(MareDbContext mareDbContext, ILogger<ConnectionHub> logger) : base(mareDbContext, logger)
        {
        }

        public async Task<ConnectionDto> Heartbeat()
        {
            var userId = Context.User!.Claims.SingleOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
            

            if (userId != null)
            {
                Logger.LogInformation("Connection from " + userId);
                var user = (await DbContext.Users.SingleAsync(u => u.UID == userId));
                return new ConnectionDto
                {
                    ServerVersion = ServerVersion,
                    UID = userId,
                    IsModerator = user.IsModerator,
                    IsAdmin = user.IsAdmin
                };
            }

            return new ConnectionDto();
        }
    }
}
