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

        public async Task<LoggedInUserDto> Heartbeat()
        {
            var userId = Context.User!.Claims.SingleOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
            Logger.LogInformation("Heartbeat from " + (userId ?? "Unknown user"));

            if (userId != null)
            {
                var user = (await DbContext.Users.SingleAsync(u => u.UID == userId));
                return new LoggedInUserDto
                {
                    UID = userId,
                    IsModerator = user.IsModerator,
                    IsAdmin = user.IsAdmin
                };
            }

            return new LoggedInUserDto();
        }
    }
}
