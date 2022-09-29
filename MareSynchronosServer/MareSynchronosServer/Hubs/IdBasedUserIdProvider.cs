using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace MareSynchronosServer.Hubs;

public class IdBasedUserIdProvider : IUserIdProvider
{
    public string GetUserId(HubConnectionContext context)
    {
        return context.User!.Claims.SingleOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
    }
}
