﻿using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Text.RegularExpressions;

namespace MareSynchronosServices.Discord;

public partial class MareWizardModule : InteractionModuleBase
{
    private ILogger<MareModule> _logger;
    private IServiceProvider _services;
    private DiscordBotServices _botServices;
    private IConfigurationService<ServerConfiguration> _mareClientConfigurationService;
    private IConfigurationService<ServicesConfiguration> _mareServicesConfiguration;
    private IConnectionMultiplexer _connectionMultiplexer;
    private Random random = new();

    public MareWizardModule(ILogger<MareModule> logger, IServiceProvider services, DiscordBotServices botServices,
        IConfigurationService<ServerConfiguration> mareClientConfigurationService,
        IConfigurationService<ServicesConfiguration> mareServicesConfiguration,
        IConnectionMultiplexer connectionMultiplexer)
    {
        _logger = logger;
        _services = services;
        _botServices = botServices;
        _mareClientConfigurationService = mareClientConfigurationService;
        _mareServicesConfiguration = mareServicesConfiguration;
        _connectionMultiplexer = connectionMultiplexer;
    }


    [ComponentInteraction("wizard-home:*")]
    public async Task StartWizard(bool init = false)
    {
        if (!init && !(await ValidateInteraction().ConfigureAwait(false))) return;

        using var mareDb = GetDbContext();
        bool hasAccount = await mareDb.LodeStoneAuth.AnyAsync(u => u.DiscordId == Context.User.Id && u.StartedAt == null).ConfigureAwait(false);

        if (init)
        {
            bool isBanned = await mareDb.BannedRegistrations.AnyAsync(u => u.DiscordIdOrLodestoneAuth == Context.User.Id.ToString()).ConfigureAwait(false);

            if (isBanned)
            {
                EmbedBuilder ebBanned = new();
                ebBanned.WithTitle("You are not welcome here");
                ebBanned.WithDescription("Your Discord account is banned");
                await RespondAsync(embed: ebBanned.Build(), ephemeral: true).ConfigureAwait(false);
                return;
            }
        }

        EmbedBuilder eb = new();
        eb.WithTitle("Welcome to the Mare Synchronos Service Bot for this server");
        eb.WithDescription("Here is what you can do:" + Environment.NewLine + Environment.NewLine
            + (!hasAccount ? string.Empty : ("- Check your account status press \"ℹ️ User Info\"" + Environment.NewLine))
            + (hasAccount ? string.Empty : ("- Register a new Mare Account press \"🌒 Register\"" + Environment.NewLine))
            + (!hasAccount ? string.Empty : ("- You lost your secret key press \"🏥 Recover\"" + Environment.NewLine))
            + (hasAccount ? string.Empty : ("- If you have changed your Discord account press \"🔗 Relink\"" + Environment.NewLine))
            + (!hasAccount ? string.Empty : ("- Create a secondary UIDs press \"2️⃣ Secondary UID\"" + Environment.NewLine))
            + (!hasAccount ? string.Empty : ("- Set a Vanity UID press \"💅 Vanity IDs\"" + Environment.NewLine))
            + (!hasAccount ? string.Empty : ("- Delete your primary or secondary accounts with \"⚠️ Delete\""))
            );
        eb.WithColor(Color.Blue);
        ComponentBuilder cb = new();
        if (!hasAccount)
        {
            cb.WithButton("Register", "wizard-register", ButtonStyle.Primary, new Emoji("🌒"));
            cb.WithButton("Relink", "wizard-relink", ButtonStyle.Secondary, new Emoji("🔗"));
        }
        else
        {
            cb.WithButton("User Info", "wizard-userinfo", ButtonStyle.Secondary, new Emoji("ℹ️"));
            cb.WithButton("Recover", "wizard-recover", ButtonStyle.Secondary, new Emoji("🏥"));
            cb.WithButton("Secondary UID", "wizard-secondary", ButtonStyle.Secondary, new Emoji("2️⃣"));
            cb.WithButton("Vanity IDs", "wizard-vanity", ButtonStyle.Secondary, new Emoji("💅"));
            cb.WithButton("Delete", "wizard-delete", ButtonStyle.Danger, new Emoji("⚠️"));
        }
        if (init)
        {
            await RespondAsync(embed: eb.Build(), components: cb.Build(), ephemeral: true).ConfigureAwait(false);
            var resp = await GetOriginalResponseAsync().ConfigureAwait(false);
            _botServices.ValidInteractions[Context.User.Id] = resp.Id;
            _logger.LogInformation("Init Msg: {id}", resp.Id);
        }
        else
        {
            await ModifyInteraction(eb, cb).ConfigureAwait(false);
        }
    }

    public class VanityUidModal : IModal
    {
        public string Title => "Set Vanity UID";

        [InputLabel("Set your Vanity UID")]
        [ModalTextInput("vanity_uid", TextInputStyle.Short, "5-15 characters, underscore, dash", 5, 15)]
        public string DesiredVanityUID { get; set; }
    }

    public class VanityGidModal : IModal
    {
        public string Title => "Set Vanity Syncshell ID";

        [InputLabel("Set your Vanity Syncshell ID")]
        [ModalTextInput("vanity_gid", TextInputStyle.Short, "5-20 characters, underscore, dash", 5, 20)]
        public string DesiredVanityGID { get; set; }
    }

    public class ConfirmDeletionModal : IModal
    {
        public string Title => "Confirm Account Deletion";

        [InputLabel("Enter \"DELETE\" in all Caps")]
        [ModalTextInput("confirmation", TextInputStyle.Short, "Enter DELETE")]
        public string Delete { get; set; }
    }

    private MareDbContext GetDbContext()
    {
        return _services.CreateScope().ServiceProvider.GetService<MareDbContext>();
    }

    private async Task<bool> ValidateInteraction()
    {
        if (Context.Interaction is not IComponentInteraction componentInteraction) return true;

        if (_botServices.ValidInteractions.TryGetValue(Context.User.Id, out ulong interactionId) && interactionId == componentInteraction.Message.Id)
        {
            return true;
        }

        EmbedBuilder eb = new();
        eb.WithTitle("Session expired");
        eb.WithDescription("This session has expired since you have either again pressed \"Start\" on the initial message or the bot has been restarted." + Environment.NewLine + Environment.NewLine
            + "Please use the newly started interaction or start a new one.");
        eb.WithColor(Color.Red);
        ComponentBuilder cb = new();
        await ModifyInteraction(eb, cb).ConfigureAwait(false);

        return false;
    }

    private void AddHome(ComponentBuilder cb)
    {
        cb.WithButton("Return to Home", "wizard-home:false", ButtonStyle.Secondary, new Emoji("🏠"));
    }

    private async Task ModifyModalInteraction(EmbedBuilder eb, ComponentBuilder cb)
    {
        await (Context.Interaction as SocketModal).UpdateAsync(m =>
        {
            m.Embed = eb.Build();
            m.Components = cb.Build();
        }).ConfigureAwait(false);
    }

    private async Task ModifyInteraction(EmbedBuilder eb, ComponentBuilder cb)
    {
        await ((Context.Interaction) as IComponentInteraction).UpdateAsync(m =>
        {
            m.Embed = eb.Build();
            m.Components = cb.Build();
        }).ConfigureAwait(false);
    }

    private async Task AddUserSelection(MareDbContext mareDb, ComponentBuilder cb, string customId)
    {
        var discordId = Context.User.Id;
        var existingAuth = await mareDb.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(e => e.DiscordId == discordId).ConfigureAwait(false);
        if (existingAuth != null)
        {
            SelectMenuBuilder sb = new();
            sb.WithPlaceholder("Select a UID");
            sb.WithCustomId(customId);
            var existingUids = await mareDb.Auth.Include(u => u.User).Where(u => u.UserUID == existingAuth.User.UID || u.PrimaryUserUID == existingAuth.User.UID)
                .OrderByDescending(u => u.PrimaryUser == null).ToListAsync().ConfigureAwait(false);
            foreach (var entry in existingUids)
            {
                sb.AddOption(string.IsNullOrEmpty(entry.User.Alias) ? entry.UserUID : entry.User.Alias,
                    entry.UserUID,
                    !string.IsNullOrEmpty(entry.User.Alias) ? entry.User.UID : null,
                    entry.PrimaryUserUID == null ? new Emoji("1️⃣") : new Emoji("2️⃣"));
            }
            cb.WithSelectMenu(sb);
        }
    }

    private async Task AddGroupSelection(MareDbContext db, ComponentBuilder cb, string customId)
    {
        var primary = (await db.LodeStoneAuth.Include(u => u.User).SingleAsync(u => u.DiscordId == Context.User.Id).ConfigureAwait(false)).User;
        var secondary = await db.Auth.Include(u => u.User).Where(u => u.PrimaryUserUID == primary.UID).Select(u => u.User).ToListAsync().ConfigureAwait(false);
        var primaryGids = (await db.Groups.Include(u => u.Owner).Where(u => u.OwnerUID == primary.UID).ToListAsync().ConfigureAwait(false));
        var secondaryGids = (await db.Groups.Include(u => u.Owner).Where(u => secondary.Select(u => u.UID).Contains(u.OwnerUID)).ToListAsync().ConfigureAwait(false));
        SelectMenuBuilder gids = new();
        if (primaryGids.Any() || secondaryGids.Any())
        {
            foreach (var item in primaryGids)
            {
                gids.AddOption(item.Alias ?? item.GID, item.GID, (item.Alias == null ? string.Empty : item.GID) + $" ({item.Owner.Alias ?? item.Owner.UID})", new Emoji("1️⃣"));
            }
            foreach (var item in secondaryGids)
            {
                gids.AddOption(item.Alias ?? item.GID, item.GID, (item.Alias == null ? string.Empty : item.GID) + $" ({item.Owner.Alias ?? item.Owner.UID})", new Emoji("2️⃣"));
            }
            gids.WithCustomId(customId);
            gids.WithPlaceholder("Select a Syncshell");
            cb.WithSelectMenu(gids);
        }
    }

    private async Task<string> GenerateLodestoneAuth(ulong discordid, string hashedLodestoneId, MareDbContext dbContext)
    {
        var auth = StringUtils.GenerateRandomString(32);
        LodeStoneAuth lsAuth = new LodeStoneAuth()
        {
            DiscordId = discordid,
            HashedLodestoneId = hashedLodestoneId,
            LodestoneAuthString = auth,
            StartedAt = DateTime.UtcNow
        };

        dbContext.Add(lsAuth);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        return (auth);
    }

    private int? ParseCharacterIdFromLodestoneUrl(string lodestoneUrl)
    {
        var regex = new Regex(@"https:\/\/(na|eu|de|fr|jp)\.finalfantasyxiv\.com\/lodestone\/character\/\d+");
        var matches = regex.Match(lodestoneUrl);
        var isLodestoneUrl = matches.Success;
        if (!isLodestoneUrl || matches.Groups.Count < 1) return null;

        lodestoneUrl = matches.Groups[0].ToString();
        var stringId = lodestoneUrl.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
        if (!int.TryParse(stringId, out int lodestoneId))
        {
            return null;
        }

        return lodestoneId;
    }
}
