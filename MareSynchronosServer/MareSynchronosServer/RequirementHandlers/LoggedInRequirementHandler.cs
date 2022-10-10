using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System;
using System.Linq;
using System.Security.Claims;
using MareSynchronosServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MareSynchronosServer.RequirementHandlers;

public class LoggedInRequirementHandler : AuthorizationHandler<LoggedInRequirement, HubInvocationContext>
{
    private GrpcClientIdentificationService identClient;
    private ILogger<LoggedInRequirementHandler> logger;

    public LoggedInRequirementHandler(GrpcClientIdentificationService identClient, ILogger<LoggedInRequirementHandler> logger)
    {
        this.identClient = identClient;
        this.logger = logger;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, LoggedInRequirement requirement, HubInvocationContext resource)
    {
        var uid = context.User.Claims.SingleOrDefault(g => string.Equals(g.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal))?.Value;
        var auth = context.User.Claims.SingleOrDefault(g => string.Equals(g.Type, ClaimTypes.Authentication, StringComparison.Ordinal))?.Value;

        if (uid == null || auth == null) context.Fail();

        var ident = identClient.GetCharacterIdentForUid(uid);

        if (ident == null) context.Fail();

        var isOnCurrent = identClient.IsOnCurrentServer(uid);
        if (!isOnCurrent) identClient.MarkUserOnline(uid, ident);

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
