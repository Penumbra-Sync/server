using MareSynchronosShared.Data;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MareSynchronosShared.RequirementHandlers;
public class ExistingUserRequirementHandler : AuthorizationHandler<ExistingUserRequirement>
{
    private readonly IDbContextFactory<MareDbContext> _dbContextFactory;
    private readonly ILogger<UserRequirementHandler> _logger;

    public ExistingUserRequirementHandler(IDbContextFactory<MareDbContext> dbContext, ILogger<UserRequirementHandler> logger)
    {
        _dbContextFactory = dbContext;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ExistingUserRequirement requirement)
    {
        var uid = context.User.Claims.SingleOrDefault(g => string.Equals(g.Type, MareClaimTypes.Uid, StringComparison.Ordinal))?.Value;
        if (uid == null) context.Fail();

        var discordIdString = context.User.Claims.SingleOrDefault(g => string.Equals(g.Type, MareClaimTypes.DiscordId, StringComparison.Ordinal))?.Value;
        if (discordIdString == null) context.Fail();

        using var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var user = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(b => b.UID == uid).ConfigureAwait(false);
        if (user == null) context.Fail();

        if (!ulong.TryParse(discordIdString, out ulong discordId)) context.Fail();

        var discordUser = await dbContext.LodeStoneAuth.AsNoTracking().SingleOrDefaultAsync(b => b.DiscordId == discordId).ConfigureAwait(false);
        if (discordUser == null) context.Fail();

        context.Succeed(requirement);
    }
}