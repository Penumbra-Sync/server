using System.Linq;
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
        public ConnectionHub(MareDbContext mareDbContext, ILogger<ConnectionHub> logger) : base(mareDbContext, logger)
        {
        }

        public async Task<UserDto> Heartbeat()
        {
            var userId = Context.User!.Claims.SingleOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
            Logger.LogInformation("Heartbeat from " + (userId ?? "Unknown user"));

            if (userId != null)
            {
                var isAdmin = (await DbContext.Users.SingleOrDefaultAsync(u => u.UID == userId))?.IsAdmin ?? false;
                return new UserDto
                {
                    UID = userId,
                    IsAdmin = isAdmin
                };
            }

            return new UserDto();
        }
    }
}
