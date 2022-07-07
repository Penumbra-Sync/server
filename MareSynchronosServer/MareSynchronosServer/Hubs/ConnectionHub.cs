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
        public async Task<ConnectionDto> Heartbeat()
        {
            var userId = Context.User!.Claims.SingleOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;

            await Clients.Caller.SendAsync(ConnectionHubAPI.OnUpdateSystemInfo, _systemInfoService.SystemInfoDto);

            if (userId != null)
            {
                Logger.LogInformation("Connection from " + userId);
                var user = (await DbContext.Users.SingleAsync(u => u.UID == userId));
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
