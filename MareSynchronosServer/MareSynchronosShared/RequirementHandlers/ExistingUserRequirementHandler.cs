using MareSynchronosShared.Data;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MareSynchronosShared.RequirementHandlers;
public class ExistingUserRequirementHandler : AuthorizationHandler<ExistingUserRequirement>
{
    private readonly IDbContextFactory<MareDbContext> _dbContextFactory;
    private readonly ILogger<ExistingUserRequirementHandler> _logger;
    private readonly static ConcurrentDictionary<string, (bool Exists, DateTime LastCheck)> _existingUserDict = [];
    private readonly static ConcurrentDictionary<ulong, (bool Exists, DateTime LastCheck)> _existingDiscordDict = [];

    public ExistingUserRequirementHandler(IDbContextFactory<MareDbContext> dbContext, ILogger<ExistingUserRequirementHandler> logger)
    {
        _dbContextFactory = dbContext;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ExistingUserRequirement requirement)
    {
        try
        {
            var uid = context.User.Claims.SingleOrDefault(g => string.Equals(g.Type, MareClaimTypes.Uid, StringComparison.Ordinal))?.Value;
            if (uid == null)
            {
                context.Fail();
                _logger.LogWarning("Failed to find UID in claims");
                return;
            }

            var discordIdString = context.User.Claims.SingleOrDefault(g => string.Equals(g.Type, MareClaimTypes.DiscordId, StringComparison.Ordinal))?.Value;
            if (discordIdString == null)
            {
                context.Fail();
                _logger.LogWarning("Failed to find DiscordId in claims");
                return;
            }
            if (!ulong.TryParse(discordIdString, out ulong discordId))
            {
                _logger.LogWarning("Failed to parse DiscordId");
                context.Fail();
                return;
            }

            using var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

            if (!_existingUserDict.TryGetValue(uid, out (bool Exists, DateTime LastCheck) existingUser)
                || DateTime.UtcNow.Subtract(existingUser.LastCheck).TotalHours > 1)
            {
                var userExists = await dbContext.Users.SingleOrDefaultAsync(context => context.UID == uid).ConfigureAwait(false) != null;
                _existingUserDict[uid] = existingUser = (userExists, DateTime.UtcNow);
            }
            if (!existingUser.Exists)
            {
                _logger.LogWarning("Failed to find Mare User {User} in DB", uid);
                context.Fail();
                return;
            }

            if (!_existingDiscordDict.TryGetValue(discordId, out (bool Exists, DateTime LastCheck) existingDiscordUser)
                || DateTime.UtcNow.Subtract(existingDiscordUser.LastCheck).TotalHours > 1)
            {
                var discordUserExists = await dbContext.LodeStoneAuth.AsNoTracking().SingleOrDefaultAsync(b => b.DiscordId == discordId).ConfigureAwait(false) != null;
                _existingDiscordDict[discordId] = existingDiscordUser = (discordUserExists, DateTime.UtcNow);
            }

            if (!existingDiscordUser.Exists)
            {
                _logger.LogWarning("Failed to find Discord User {User} in DB", discordId);
                context.Fail();
                return;
            }

            context.Succeed(requirement);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "ExistingUserRequirementHandler failed");
        }
    }
}