using System;
using System.Linq;
using System.Runtime.ConstrainedExecution;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosServer.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Hubs
{
    public class ConnectionHub : BaseHub<ConnectionHub>
    {
        private readonly SystemInfoService _systemInfoService;

        public ConnectionHub(MareDbContext mareDbContext, ILogger<ConnectionHub> logger, SystemInfoService systemInfoService) : base(mareDbContext, logger)
        {
            _systemInfoService = systemInfoService;
        }

        [HubMethodName(ConnectionHubAPI.InvokeHeartbeat)]
        public async Task<ConnectionDto> Heartbeat(string? characterIdentification)
        {
            var userId = Context.User!.Claims.SingleOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;

            Logger.LogInformation("Connection from " + userId + ", CI: " + characterIdentification);

            await Clients.Caller.SendAsync(ConnectionHubAPI.OnUpdateSystemInfo, _systemInfoService.SystemInfoDto);

            var isBanned = await DbContext.BannedUsers.AsNoTracking().AnyAsync(u => u.CharacterIdentification == characterIdentification);

            if (userId != null && !isBanned && !string.IsNullOrEmpty(characterIdentification))
            {
                Logger.LogInformation("Connection from " + userId);
                var user = (await DbContext.Users.SingleAsync(u => u.UID == userId));
                user.CharacterIdentification = characterIdentification;
                await DbContext.SaveChangesAsync();
                return new ConnectionDto
                {
                    ServerVersion = API.Version,
                    UID = userId,
                    IsModerator = user.IsModerator,
                    IsAdmin = user.IsAdmin
                };
            }

            return new ConnectionDto()
            {
                ServerVersion = API.Version
            };
        }

        [HubMethodName(ConnectionHubAPI.InvokeGetSystemInfo)]
        public async Task<SystemInfoDto> GetSystemInfo()
        {
            return _systemInfoService.SystemInfoDto;
        }
    }
}
