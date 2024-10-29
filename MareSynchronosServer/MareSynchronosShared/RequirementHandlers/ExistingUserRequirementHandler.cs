using MareSynchronosShared.Data;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MareSynchronosShared.RequirementHandlers;
public class ExistingUserRequirementHandler : AuthorizationHandler<ExistingUserRequirement>
{
    private readonly MareDbContext _dbContext;
    private readonly ILogger<UserRequirementHandler> _logger;

    public ExistingUserRequirementHandler(MareDbContext dbContext, ILogger<UserRequirementHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ExistingUserRequirement requirement)
    {
        var uid = context.User.Claims.SingleOrDefault(g => string.Equals(g.Type, MareClaimTypes.Uid, StringComparison.Ordinal))?.Value;
        if (uid == null) context.Fail();

        var discordIdString = context.User.Claims.SingleOrDefault(g => string.Equals(g.Type, MareClaimTypes.DiscordId, StringComparison.Ordinal))?.Value;
        if (discordIdString == null) context.Fail();

        var user = await _dbContext.Users.AsNoTracking().SingleOrDefaultAsync(b => b.UID == uid).ConfigureAwait(false);
        if (user == null) context.Fail();

        if (!ulong.TryParse(discordIdString, out ulong discordId)) context.Fail();

        var discordUser = _dbContext.LodeStoneAuth.AsNoTracking().SingleOrDefaultAsync(b => b.DiscordId == discordId);
        if (discordUser == null) context.Fail();

        context.Succeed(requirement);
    }
}