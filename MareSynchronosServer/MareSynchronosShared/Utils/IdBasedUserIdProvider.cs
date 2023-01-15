using Microsoft.AspNetCore.SignalR;

namespace MareSynchronosShared.Utils;

public class IdBasedUserIdProvider : IUserIdProvider
{
    public string GetUserId(HubConnectionContext context)
    {
        return context.User!.Claims.SingleOrDefault(c => string.Equals(c.Type, MareClaimTypes.Uid, StringComparison.Ordinal))?.Value;
    }
}
