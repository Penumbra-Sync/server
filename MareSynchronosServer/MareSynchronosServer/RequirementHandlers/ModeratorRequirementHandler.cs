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

public class ModeratorRequirementHandler : AuthorizationHandler<ModeratorRequirement, HubInvocationContext>
{
    private readonly MareDbContext dbContext;
    private readonly ILogger<ModeratorRequirementHandler> logger;

    public ModeratorRequirementHandler(MareDbContext dbContext, ILogger<ModeratorRequirementHandler> logger)
    {
        this.dbContext = dbContext;
        this.logger = logger;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ModeratorRequirement requirement, HubInvocationContext resource)
    {
        var uid = context.User.Claims.SingleOrDefault(g => string.Equals(g.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal))?.Value;
        var auth = context.User.Claims.SingleOrDefault(g => string.Equals(g.Type, ClaimTypes.Authentication, StringComparison.Ordinal))?.Value;

        if (uid == null || auth == null) context.Fail();

        var user = await dbContext.Users.SingleOrDefaultAsync(b => b.UID == uid).ConfigureAwait(false);
        if (user == null || !user.IsAdmin && !user.IsModerator) context.Fail();

        logger.LogInformation("Admin/Moderator {uid} authenticated", uid);

        context.Succeed(requirement);
    }
}
