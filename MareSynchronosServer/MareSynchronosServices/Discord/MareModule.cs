using Discord;
using Discord.Interactions;
using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using MareSynchronosShared.Models;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Services;
using StackExchange.Redis;
using MareSynchronos.API.Data.Enum;
using MareSynchronosShared.Utils.Configuration;

namespace MareSynchronosServices.Discord;

public class MareModule : InteractionModuleBase
{
    private readonly ILogger<MareModule> _logger;
    private readonly IServiceProvider _services;
    private readonly IConfigurationService<ServicesConfiguration> _mareServicesConfiguration;
    private readonly IConnectionMultiplexer _connectionMultiplexer;

    public MareModule(ILogger<MareModule> logger, IServiceProvider services,
        IConfigurationService<ServicesConfiguration> mareServicesConfiguration,
        IConnectionMultiplexer connectionMultiplexer)
    {
        _logger = logger;
        _services = services;
        _mareServicesConfiguration = mareServicesConfiguration;
        _connectionMultiplexer = connectionMultiplexer;
    }

    [SlashCommand("userinfo", "Shows you your user information")]
    public async Task UserInfo([Summary("secondary_uid", "(Optional) Your secondary UID")] string? secondaryUid = null,
        [Summary("discord_user", "ADMIN ONLY: Discord User to check for")] IUser? discordUser = null,
        [Summary("uid", "ADMIN ONLY: UID to check for")] string? uid = null)
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}",
            Context.Interaction.User.Id, nameof(UserInfo));

        try
        {
            EmbedBuilder eb = new();

            eb = await HandleUserInfo(eb, Context.User.Id, secondaryUid, discordUser?.Id ?? null, uid);

            await RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EmbedBuilder eb = new();
            eb.WithTitle("An error occured");
            eb.WithDescription("Please report this error to bug-reports: " + Environment.NewLine + ex.Message + Environment.NewLine + ex.StackTrace + Environment.NewLine);

            await RespondAsync(embeds: new Embed[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
        }
    }

    [SlashCommand("useradd", "ADMIN ONLY: add a user unconditionally to the Database")]
    public async Task UserAdd([Summary("desired_uid", "Desired UID")] string desiredUid)
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}:{params}",
            Context.Interaction.User.Id, nameof(UserAdd),
            string.Join(",", new[] { $"{nameof(desiredUid)}:{desiredUid}" }));

        try
        {
            var embed = await HandleUserAdd(desiredUid, Context.User.Id);

            await RespondAsync(embeds: new[] { embed }, ephemeral: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EmbedBuilder eb = new();
            eb.WithTitle("An error occured");
            eb.WithDescription("Please report this error to bug-reports: " + Environment.NewLine + ex.Message + Environment.NewLine + ex.StackTrace + Environment.NewLine);

            await RespondAsync(embeds: new Embed[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
        }
    }

    [SlashCommand("message", "ADMIN ONLY: sends a message to clients")]
    public async Task SendMessageToClients([Summary("message", "Message to send")] string message,
        [Summary("severity", "Severity of the message")] MessageSeverity messageType = MessageSeverity.Information,
        [Summary("uid", "User ID to the person to send the message to")] string? uid = null)
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}:{message}:{type}:{uid}", Context.Interaction.User.Id, nameof(SendMessageToClients), message, messageType, uid);

        using var scope = _services.CreateScope();
        using var db = scope.ServiceProvider.GetService<MareDbContext>();

        if (!(await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(a => a.DiscordId == Context.Interaction.User.Id))?.User?.IsAdmin ?? true)
        {
            await RespondAsync("No permission", ephemeral: true).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrEmpty(uid) && !await db.Users.AnyAsync(u => u.UID == uid))
        {
            await RespondAsync("Specified UID does not exist", ephemeral: true).ConfigureAwait(false);
            return;
        }

        try
        {
            using HttpClient c = new HttpClient();
            await c.PostAsJsonAsync(new Uri(_mareServicesConfiguration.GetValue<Uri>
                (nameof(ServicesConfiguration.MainServerAddress)), "/msgc/sendMessage"), new ClientMessage(messageType, message, uid ?? string.Empty))
                .ConfigureAwait(false);

            var discordChannelForMessages = _mareServicesConfiguration.GetValueOrDefault<ulong?>(nameof(ServicesConfiguration.DiscordChannelForMessages), null);
            if (uid == null && discordChannelForMessages != null)
            {
                var discordChannel = await Context.Guild.GetChannelAsync(discordChannelForMessages.Value) as IMessageChannel;
                if (discordChannel != null)
                {
                    var embedColor = messageType switch
                    {
                        MessageSeverity.Information => Color.Blue,
                        MessageSeverity.Warning => new Color(255, 255, 0),
                        MessageSeverity.Error => Color.Red,
                        _ => Color.Blue
                    };

                    EmbedBuilder eb = new();
                    eb.WithTitle(messageType + " server message");
                    eb.WithColor(embedColor);
                    eb.WithDescription(message);

                    await discordChannel.SendMessageAsync(embed: eb.Build());
                }
            }

            await RespondAsync("Message sent", ephemeral: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RespondAsync("Failed to send message: " + ex.ToString(), ephemeral: true).ConfigureAwait(false);
        }
    }

    public async Task<Embed> HandleUserAdd(string desiredUid, ulong discordUserId)
    {
        var embed = new EmbedBuilder();

        using var scope = _services.CreateScope();
        using var db = scope.ServiceProvider.GetService<MareDbContext>();
        if (!(await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(a => a.DiscordId == discordUserId))?.User?.IsAdmin ?? true)
        {
            embed.WithTitle("Failed to add user");
            embed.WithDescription("No permission");
        }
        else if (db.Users.Any(u => u.UID == desiredUid || u.Alias == desiredUid))
        {
            embed.WithTitle("Failed to add user");
            embed.WithDescription("Already in Database");
        }
        else
        {
            User newUser = new()
            {
                IsAdmin = false,
                IsModerator = false,
                LastLoggedIn = DateTime.UtcNow,
                UID = desiredUid,
            };

            var computedHash = StringUtils.Sha256String(StringUtils.GenerateRandomString(64) + DateTime.UtcNow.ToString());
            var auth = new Auth()
            {
                HashedKey = StringUtils.Sha256String(computedHash),
                User = newUser,
            };

            await db.Users.AddAsync(newUser);
            await db.Auth.AddAsync(auth);

            await db.SaveChangesAsync();

            embed.WithTitle("Successfully added " + desiredUid);
            embed.WithDescription("Secret Key: " + computedHash);
        }

        return embed.Build();
    }

    private async Task<EmbedBuilder> HandleUserInfo(EmbedBuilder eb, ulong id, string? secondaryUserUid = null, ulong? optionalUser = null, string? uid = null)
    {
        bool showForSecondaryUser = secondaryUserUid != null;
        using var scope = _services.CreateScope();
        await using var db = scope.ServiceProvider.GetRequiredService<MareDbContext>();

        var primaryUser = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.DiscordId == id).ConfigureAwait(false);

        ulong userToCheckForDiscordId = id;

        if (primaryUser == null)
        {
            eb.WithTitle("No account");
            eb.WithDescription("No Mare account was found associated to your Discord user");
            return eb;
        }

        bool isAdminCall = primaryUser.User.IsModerator || primaryUser.User.IsAdmin;

        if ((optionalUser != null || uid != null) && !isAdminCall)
        {
            eb.WithTitle("Unauthorized");
            eb.WithDescription("You are not authorized to view another users' information");
            return eb;
        }
        else if ((optionalUser != null || uid != null) && isAdminCall)
        {
            LodeStoneAuth userInDb = null;
            if (optionalUser != null)
            {
                userInDb = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.DiscordId == optionalUser).ConfigureAwait(false);
            }
            else if (uid != null)
            {
                userInDb = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.User.UID == uid || u.User.Alias == uid).ConfigureAwait(false);
            }

            if (userInDb == null)
            {
                eb.WithTitle("No account");
                eb.WithDescription("The Discord user has no valid Mare account");
                return eb;
            }

            userToCheckForDiscordId = userInDb.DiscordId;
        }

        var lodestoneUser = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.DiscordId == userToCheckForDiscordId).ConfigureAwait(false);
        var dbUser = lodestoneUser.User;
        if (showForSecondaryUser)
        {
            dbUser = (await db.Auth.Include(u => u.User).SingleOrDefaultAsync(u => u.PrimaryUserUID == dbUser.UID && u.UserUID == secondaryUserUid))?.User;
            if (dbUser == null)
            {
                eb.WithTitle("No such secondary UID");
                eb.WithDescription($"A secondary UID {secondaryUserUid} was not found attached to your primary UID {primaryUser.User.UID}.");
                return eb;
            }
        }

        var auth = await db.Auth.Include(u => u.PrimaryUser).SingleOrDefaultAsync(u => u.UserUID == dbUser.UID).ConfigureAwait(false);
        var groups = await db.Groups.Where(g => g.OwnerUID == dbUser.UID).ToListAsync().ConfigureAwait(false);
        var groupsJoined = await db.GroupPairs.Where(g => g.GroupUserUID == dbUser.UID).ToListAsync().ConfigureAwait(false);
        var identity = await _connectionMultiplexer.GetDatabase().StringGetAsync("UID:" + dbUser.UID).ConfigureAwait(false);

        eb.WithTitle("User Information");
        eb.WithDescription("This is the user information for Discord User <@" + userToCheckForDiscordId + ">" + Environment.NewLine + Environment.NewLine
            + "If you want to verify your secret key is valid, go to https://emn178.github.io/online-tools/sha256.html and copy your secret key into there and compare it to the Hashed Secret Key provided below.");
        eb.AddField("UID", dbUser.UID);
        if (!string.IsNullOrEmpty(dbUser.Alias))
        {
            eb.AddField("Vanity UID", dbUser.Alias);
        }
        if (showForSecondaryUser)
        {
            eb.AddField("Primary UID for " + dbUser.UID, auth.PrimaryUserUID);
        }
        else
        {
            var secondaryUIDs = await db.Auth.Where(p => p.PrimaryUserUID == dbUser.UID).Select(p => p.UserUID).ToListAsync();
            if (secondaryUIDs.Any())
            {
                eb.AddField("Secondary UIDs", string.Join(Environment.NewLine, secondaryUIDs));
            }
        }
        eb.AddField("Last Online (UTC)", dbUser.LastLoggedIn.ToString("U"));
        eb.AddField("Currently online ", !string.IsNullOrEmpty(identity));
        eb.AddField("Hashed Secret Key", auth.HashedKey);
        eb.AddField("Joined Syncshells", groupsJoined.Count);
        eb.AddField("Owned Syncshells", groups.Count);
        foreach (var group in groups)
        {
            var syncShellUserCount = await db.GroupPairs.CountAsync(g => g.GroupGID == group.GID).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(group.Alias))
            {
                eb.AddField("Owned Syncshell " + group.GID + " Vanity ID", group.Alias);
            }
            eb.AddField("Owned Syncshell " + group.GID + " User Count", syncShellUserCount);
        }

        if (isAdminCall && !string.IsNullOrEmpty(identity))
        {
            eb.AddField("Character Ident", identity);
        }

        return eb;
    }
}
