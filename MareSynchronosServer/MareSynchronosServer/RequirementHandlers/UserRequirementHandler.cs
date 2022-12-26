using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;
using MareSynchronosServer.Services;

namespace MareSynchronosServer.RequirementHandlers;

public class UserRequirementHandler : AuthorizationHandler<UserRequirement, HubInvocationContext>
{
    private readonly IClientIdentificationService identClient;
    private readonly MareDbContext dbContext;
    private readonly ILogger<UserRequirementHandler> logger;

    public UserRequirementHandler(IClientIdentificationService identClient, MareDbContext dbContext, ILogger<UserRequirementHandler> logger)
    {
        this.identClient = identClient;
        this.dbContext = dbContext;
        this.logger = logger;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, UserRequirement requirement, HubInvocationContext resource)
    {
        var uid = context.User.Claims.SingleOrDefault(g => string.Equals(g.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal))?.Value;
        var auth = context.User.Claims.SingleOrDefault(g => string.Equals(g.Type, ClaimTypes.Authentication, StringComparison.Ordinal))?.Value;

        if (uid == null || auth == null) context.Fail();

        if ((requirement.Requirements & UserRequirements.Identified) is UserRequirements.Identified)
        {
            var ident = identClient.GetCharacterIdentForUid(uid);
            if (ident == null) context.Fail();

            var isOnCurrent = identClient.IsOnCurrentServer(uid);
            if (!isOnCurrent) identClient.MarkUserOnline(uid, ident);
        }

        if ((requirement.Requirements & UserRequirements.Administrator) is UserRequirements.Administrator)
        {
            var user = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(b => b.UID == uid).ConfigureAwait(false);
            if (user == null || !user.IsAdmin) context.Fail();
            logger.LogInformation("Admin {uid} authenticated", uid);
        }

        if ((requirement.Requirements & UserRequirements.Moderator) is UserRequirements.Moderator)
        {
            var user = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(b => b.UID == uid).ConfigureAwait(false);
            if (user == null || !user.IsAdmin && !user.IsModerator) context.Fail();
            logger.LogInformation("Admin/Moderator {uid} authenticated", uid);
        }

        context.Succeed(requirement);
    }
}
