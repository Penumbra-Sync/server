using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;
using MareSynchronosShared.Utils;
using StackExchange.Redis;
using Microsoft.Extensions.Logging;

namespace MareSynchronosShared.RequirementHandlers;

public class RedisDbUserRequirementHandler : AuthorizationHandler<UserRequirement, HubInvocationContext>
{
    private readonly IDbContextFactory<MareDbContext> _dbContextFactory;
    private readonly ILogger<RedisDbUserRequirementHandler> _logger;
    private readonly IDatabase _redis;

    public RedisDbUserRequirementHandler(IDbContextFactory<MareDbContext> dbContextFactory, ILogger<RedisDbUserRequirementHandler> logger, IDatabase redisDb)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _redis = redisDb;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, UserRequirement requirement, HubInvocationContext resource)
    {
        var uid = context.User.Claims.SingleOrDefault(g => string.Equals(g.Type, MareClaimTypes.Uid, StringComparison.Ordinal))?.Value;

        if (uid == null) context.Fail();

        if ((requirement.Requirements & UserRequirements.Identified) is UserRequirements.Identified)
        {
            var ident = await _redis.StringGetAsync("UID:" + uid).ConfigureAwait(false);
            if (ident == RedisValue.EmptyString) context.Fail();
        }

        if ((requirement.Requirements & UserRequirements.Administrator) is UserRequirements.Administrator)
        {
            using var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            var user = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(b => b.UID == uid).ConfigureAwait(false);
            if (user == null || !user.IsAdmin) context.Fail();
            _logger.LogInformation("Admin {uid} authenticated", uid);
        }

        if ((requirement.Requirements & UserRequirements.Moderator) is UserRequirements.Moderator)
        {
            using var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            var user = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(b => b.UID == uid).ConfigureAwait(false);
            if (user == null || !user.IsAdmin && !user.IsModerator) context.Fail();
            _logger.LogInformation("Admin/Moderator {uid} authenticated", uid);
        }

        context.Succeed(requirement);
    }
}
