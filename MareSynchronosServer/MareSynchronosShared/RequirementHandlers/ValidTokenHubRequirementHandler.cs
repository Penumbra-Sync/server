﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MareSynchronosShared.Utils;
using System.Globalization;

namespace MareSynchronosShared.RequirementHandlers;

public class ValidTokenRequirementHandler : AuthorizationHandler<ValidTokenRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ValidTokenRequirement requirement)
    {
        var expirationClaimValue = context.User.Claims.Single(r => string.Equals(r.Type, MareClaimTypes.Expires, StringComparison.Ordinal))?.Value;
        if (expirationClaimValue == null)
        {
            context.Fail();
        }

        DateTime expirationDate = new(long.Parse(expirationClaimValue, CultureInfo.InvariantCulture), DateTimeKind.Utc);
        if (expirationDate < DateTime.UtcNow)
        {
            context.Fail();
        }

        context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

public class ValidTokenHubRequirementHandler : AuthorizationHandler<ValidTokenRequirement, HubInvocationContext>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ValidTokenRequirement requirement, HubInvocationContext resource)
    {
        var expirationClaimValue = context.User.Claims.Single(r => string.Equals(r.Type, MareClaimTypes.Expires, StringComparison.Ordinal))?.Value;
        if (expirationClaimValue == null)
        {
            context.Fail();
        }

        DateTime expirationDate = new(long.Parse(expirationClaimValue, CultureInfo.InvariantCulture), DateTimeKind.Utc);
        if (expirationDate < DateTime.UtcNow)
        {
            context.Fail();
        }

        context.Succeed(requirement);

        return Task.CompletedTask;
    }
}