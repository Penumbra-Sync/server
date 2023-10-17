using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MareSynchronosShared.Utils;
using System.Globalization;

namespace MareSynchronosShared.RequirementHandlers;

public class ValidTokenRequirementHandler : AuthorizationHandler<ValidTokenRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ValidTokenRequirement requirement)
    {
        var expirationClaimValue = context.User.Claims.SingleOrDefault(r => string.Equals(r.Type, MareClaimTypes.Expires, StringComparison.Ordinal));
        if (expirationClaimValue == null)
        {
            context.Fail();
            return Task.CompletedTask;
        }

        DateTime expirationDate = new(long.Parse(expirationClaimValue.Value, CultureInfo.InvariantCulture), DateTimeKind.Utc);
        if (expirationDate < DateTime.UtcNow)
        {
            context.Fail();
            return Task.CompletedTask;
        }

        context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

public class ValidTokenHubRequirementHandler : AuthorizationHandler<ValidTokenRequirement, HubInvocationContext>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ValidTokenRequirement requirement, HubInvocationContext resource)
    {
        var expirationClaimValue = context.User.Claims.SingleOrDefault(r => string.Equals(r.Type, MareClaimTypes.Expires, StringComparison.Ordinal));
        if (expirationClaimValue == null)
        {
            context.Fail();
            return Task.CompletedTask;
        }

        DateTime expirationDate = new(long.Parse(expirationClaimValue.Value, CultureInfo.InvariantCulture), DateTimeKind.Utc);
        if (expirationDate < DateTime.UtcNow)
        {
            context.Fail();
            return Task.CompletedTask;
        }

        context.Succeed(requirement);

        return Task.CompletedTask;
    }
}