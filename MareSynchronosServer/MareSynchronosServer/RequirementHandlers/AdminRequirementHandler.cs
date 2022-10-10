using System.Threading.Tasks;
using System;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.RequirementHandlers;

public class AdminRequirementHandler : AuthorizationHandler<AdminRequirement, HubInvocationContext>
{
    private readonly MareDbContext dbContext;
    private readonly ILogger<AdminRequirementHandler> logger;

    public AdminRequirementHandler(MareDbContext dbContext, ILogger<AdminRequirementHandler> logger)
    {
        this.dbContext = dbContext;
        this.logger = logger;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement, HubInvocationContext resource)
    {
        var uid = context.User.Claims.SingleOrDefault(g => string.Equals(g.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal))?.Value;
        var auth = context.User.Claims.SingleOrDefault(g => string.Equals(g.Type, ClaimTypes.Authentication, StringComparison.Ordinal))?.Value;

        if (uid == null || auth == null) context.Fail();

        var isAdmin = (await dbContext.Users.SingleOrDefaultAsync(b => b.UID == uid).ConfigureAwait(false))?.IsAdmin;
        if (isAdmin == null || (!isAdmin ?? true)) context.Fail();

        logger.LogInformation("Admin {uid} authenticated", uid);

        context.Succeed(requirement);
    }
}
