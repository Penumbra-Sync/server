using Discord;
using Discord.Interactions;
using MareSynchronosShared.Data;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Discord.WebSocket;
using Prometheus;
using MareSynchronosShared.Models;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Services;
using static MareSynchronosShared.Protos.IdentificationService;
using static System.Formats.Asn1.AsnWriter;

namespace MareSynchronosServices.Discord;

public class LodestoneModal : IModal
{
    public string Title => "Verify with Lodestone";

    [InputLabel("Enter the Lodestone URL of your Character")]
    [ModalTextInput("lodestone_url", TextInputStyle.Short, "https://*.finalfantasyxiv.com/lodestone/character/<CHARACTERID>/")]
    public string LodestoneUrl { get; set; }
}

public class MareModule : InteractionModuleBase
{
    private readonly ILogger<MareModule> _logger;
    private readonly IServiceProvider _services;
    private readonly DiscordBotServices _botServices;
    private readonly IdentificationServiceClient _identificationServiceClient;
    private readonly IConfigurationService<ServerConfiguration> _mareClientConfigurationService;
    private Random random = new();

    public MareModule(ILogger<MareModule> logger, IServiceProvider services, DiscordBotServices botServices,
        IdentificationServiceClient identificationServiceClient, IConfigurationService<ServerConfiguration> mareClientConfigurationService)
    {
        _logger = logger;
        _services = services;
        _botServices = botServices;
        _identificationServiceClient = identificationServiceClient;
        _mareClientConfigurationService = mareClientConfigurationService;
    }

    [SlashCommand("register", "Starts the registration process for the Mare Synchronos server of this Discord")]
    public async Task Register([Summary("overwrite", "Overwrites your old account")] bool overwrite = false)
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}:{params}",
            Context.Client.CurrentUser.Id, nameof(Register),
            string.Join(",", new[] { $"{nameof(overwrite)}:{overwrite}" }));

        await TryRespondAsync(async () =>
        {
            if (overwrite)
            {
                await DeletePreviousUserAccount(Context.User.Id).ConfigureAwait(false);
            }

            await RespondWithModalAsync<LodestoneModal>("register_modal").ConfigureAwait(false);
        });
    }

    [SlashCommand("setvanityuid", "Sets your Vanity UID.")]
    public async Task SetVanityUid([Summary("vanity_uid", "Desired Vanity UID")] string vanityUid)
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}:{params}",
            Context.Client.CurrentUser.Id, nameof(SetVanityUid),
            string.Join(",", new[] { $"{nameof(vanityUid)}:{vanityUid}" }));

        await TryRespondAsync(async () =>
        {
            EmbedBuilder eb = new();

            eb = await HandleVanityUid(eb, Context.User.Id, vanityUid);

            await RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
        });
    }

    [SlashCommand("setsyncshellvanityid", "Sets a Vanity GID for a Syncshell")]
    public async Task SetSyncshellVanityId(
        [Summary("syncshell_id", "Syncshell ID")] string syncshellId,
        [Summary("vanity_syncshell_id", "Desired Vanity Syncshell ID")] string vanityId)
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}:{params}",
            Context.Client.CurrentUser.Id, nameof(SetSyncshellVanityId),
            string.Join(",", new[] { $"{nameof(syncshellId)}:{syncshellId}", $"{nameof(vanityId)}:{vanityId}" }));

        await TryRespondAsync(async () =>
        {
            EmbedBuilder eb = new();

            eb = await HandleVanityGid(eb, Context.User.Id, syncshellId, vanityId);

            await RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
        });
    }

    [SlashCommand("verify", "Finishes the registration process for the Mare Synchronos server of this Discord")]
    public async Task Verify()
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}",
            Context.Client.CurrentUser.Id, nameof(Verify));
        await TryRespondAsync(async () =>
        {
            EmbedBuilder eb = new();
            if (_botServices.VerificationQueue.Any(u => u.Key == Context.User.Id))
            {
                eb.WithTitle("Already queued for verfication");
                eb.WithDescription("You are already queued for verification. Please wait.");
                await RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
            }
            else if (!_botServices.DiscordLodestoneMapping.ContainsKey(Context.User.Id))
            {
                eb.WithTitle("Cannot verify registration");
                eb.WithDescription("You need to **/register** first before you can **/verify**");
                await RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
            }
            else
            {
                await DeferAsync(ephemeral: true).ConfigureAwait(false);
                _botServices.VerificationQueue.Enqueue(new KeyValuePair<ulong, Action<IServiceProvider>>(Context.User.Id, async (sp) => await HandleVerifyAsync((SocketSlashCommand)Context.Interaction, sp)));
            }
        });
    }

    [SlashCommand("verify_relink", "Finishes the relink process for your user on the Mare Synchronos server of this Discord")]
    public async Task VerifyRelink()
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}",
            Context.Client.CurrentUser.Id, nameof(VerifyRelink));
        await TryRespondAsync(async () =>
        {
            EmbedBuilder eb = new();
            if (_botServices.VerificationQueue.Any(u => u.Key == Context.User.Id))
            {
                eb.WithTitle("Already queued for verfication");
                eb.WithDescription("You are already queued for verification. Please wait.");
                await RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
            }
            else if (!_botServices.DiscordRelinkLodestoneMapping.ContainsKey(Context.User.Id))
            {
                eb.WithTitle("Cannot verify relink");
                eb.WithDescription("You need to **/relink** first before you can **/verify_relink**");
                await RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
            }
            else
            {
                await DeferAsync(ephemeral: true).ConfigureAwait(false);
                _botServices.VerificationQueue.Enqueue(new KeyValuePair<ulong, Action<IServiceProvider>>(Context.User.Id, async (sp) => await HandleVerifyRelinkAsync((SocketSlashCommand)Context.Interaction, sp)));
            }
        });
    }

    [SlashCommand("recover", "Allows you to recover your account by generating a new secret key")]
    public async Task Recover()
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}",
            Context.Client.CurrentUser.Id, nameof(Recover));
        await RespondWithModalAsync<LodestoneModal>("recover_modal").ConfigureAwait(false);
    }

    [SlashCommand("userinfo", "Shows you your user information")]
    public async Task UserInfo(
        [Summary("discord_user", "ADMIN ONLY: Discord User to check for")] IUser? discordUser = null,
        [Summary("uid", "ADMIN ONLY: UID to check for")] string? uid = null)
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}",
            Context.Client.CurrentUser.Id, nameof(UserInfo));

        await TryRespondAsync(async () =>
        {
            EmbedBuilder eb = new();

            eb = await HandleUserInfo(eb, Context.User.Id, discordUser?.Id ?? null, uid);

            await RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
        });
    }

    [SlashCommand("relink", "Allows you to link a new Discord account to an existing Mare account")]
    public async Task Relink()
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}",
            Context.Client.CurrentUser.Id, nameof(Relink));
        await RespondWithModalAsync<LodestoneModal>("relink_modal").ConfigureAwait(false);
    }

    [SlashCommand("useradd", "ADMIN ONLY: add a user unconditionally to the Database")]
    public async Task UserAdd([Summary("desired_uid", "Desired UID")] string desiredUid)
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}:{params}",
            Context.Client.CurrentUser.Id, nameof(UserAdd),
            string.Join(",", new[] { $"{nameof(desiredUid)}:{desiredUid}" }));

        await TryRespondAsync(async () =>
        {
            var embed = await HandleUserAdd(desiredUid, Context.User.Id);

            await RespondAsync(embeds: new[] { embed }, ephemeral: true).ConfigureAwait(false);
        });
    }

    [ModalInteraction("recover_modal")]
    public async Task RecoverModal(LodestoneModal modal)
    {
        _logger.LogInformation("Modal:{userId}:{Method}",
            Context.Client.CurrentUser.Id, nameof(RecoverModal));

        await TryRespondAsync(async () =>
        {
            var embed = await HandleRecoverModalAsync(modal, Context.User.Id).ConfigureAwait(false);
            await RespondAsync(embeds: new Embed[] { embed }, ephemeral: true).ConfigureAwait(false);
        });
    }

    [ModalInteraction("register_modal")]
    public async Task RegisterModal(LodestoneModal modal)
    {
        _logger.LogInformation("Modal:{userId}:{Method}",
            Context.Client.CurrentUser.Id, nameof(RegisterModal));

        await TryRespondAsync(async () =>
        {
            var embed = await HandleRegisterModalAsync(modal, Context.User.Id).ConfigureAwait(false);
            await RespondAsync(embeds: new Embed[] { embed }, ephemeral: true).ConfigureAwait(false);
        });
    }

    [ModalInteraction("relink_modal")]
    public async Task RelinkModal(LodestoneModal modal)
    {
        _logger.LogInformation("Modal:{userId}:{Method}",
            Context.Client.CurrentUser.Id, nameof(RelinkModal));

        await TryRespondAsync(async () =>
        {
            var embed = await HandleRelinkModalAsync(modal, Context.User.Id).ConfigureAwait(false);
            await RespondAsync(embeds: new Embed[] { embed }, ephemeral: true).ConfigureAwait(false);
        });
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

    private async Task TryRespondAsync(Action act)
    {
        try
        {
            act();
        }
        catch (Exception ex)
        {
            EmbedBuilder eb = new();
            eb.WithTitle("An error occured");
            eb.WithDescription("Please report this error to bug-reports: " + Environment.NewLine + ex.Message + Environment.NewLine + ex.StackTrace + Environment.NewLine);

            await RespondAsync(embeds: new Embed[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
        }
    }

    private async Task<EmbedBuilder> HandleUserInfo(EmbedBuilder eb, ulong id, ulong? optionalUser = null, string? uid = null)
    {
        using var scope = _services.CreateScope();
        await using var db = scope.ServiceProvider.GetRequiredService<MareDbContext>();

        var self = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.DiscordId == id).ConfigureAwait(false);
        ulong userToCheckForDiscordId = id;

        if (self == null)
        {
            eb.WithTitle("No account");
            eb.WithDescription("No Mare account was found associated to your Discord user");
            return eb;
        }

        bool isAdminCall = self.User.IsModerator || self.User.IsAdmin;

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
        var auth = await db.Auth.SingleOrDefaultAsync(u => u.UserUID == dbUser.UID).ConfigureAwait(false);
        var identity = await _identificationServiceClient.GetIdentForUidAsync(new MareSynchronosShared.Protos.UidMessage { Uid = dbUser.UID });
        var groups = await db.Groups.Where(g => g.OwnerUID == dbUser.UID).ToListAsync().ConfigureAwait(false);
        var groupsJoined = await db.GroupPairs.Where(g => g.GroupUserUID == dbUser.UID).ToListAsync().ConfigureAwait(false);

        eb.WithTitle("User Information");
        eb.WithDescription("This is the user information for Discord User Id " + userToCheckForDiscordId + Environment.NewLine
            + "If you want to verify your secret key is valid, go to https://emn178.github.io/online-tools/sha256.html and copy your secret key into there and compare it to the Hashed Secret Key.");
        eb.AddField("UID", dbUser.UID);
        if (!string.IsNullOrEmpty(dbUser.Alias))
        {
            eb.AddField("Vanity UID", dbUser.Alias);
        }
        eb.AddField("Last Online (UTC)", dbUser.LastLoggedIn.ToString("U"));
        eb.AddField("Currently online: ", !string.IsNullOrEmpty(identity.Ident));
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

        if (isAdminCall && !string.IsNullOrEmpty(identity.Ident))
        {
            eb.AddField("Character Ident", identity.Ident);
        }

        return eb;
    }

    private async Task<Embed> HandleRecoverModalAsync(LodestoneModal arg, ulong userid)
    {
        var embed = new EmbedBuilder();

        var lodestoneId = ParseCharacterIdFromLodestoneUrl(arg.LodestoneUrl);
        if (lodestoneId == null)
        {
            embed.WithTitle("Invalid Lodestone URL");
            embed.WithDescription("The lodestone URL was not valid. It should have following format:" + Environment.NewLine
                + "https://eu.finalfantasyxiv.com/lodestone/character/YOUR_LODESTONE_ID/");
        }
        else
        {
            using var scope = _services.CreateScope();

            var hashedLodestoneId = StringUtils.Sha256String(lodestoneId.ToString());

            await using var db = scope.ServiceProvider.GetService<MareDbContext>();
            var existingLodestoneAuth = await db.LodeStoneAuth.Include("User")
                .FirstOrDefaultAsync(a => a.DiscordId == userid && a.HashedLodestoneId == hashedLodestoneId)
                .ConfigureAwait(false);

            // check if discord id or lodestone id is banned
            if (existingLodestoneAuth == null || existingLodestoneAuth.User == null)
            {
                embed.WithTitle("Recovery failed");
                embed.WithDescription("This DiscordID or Lodestone account pair does not exist.");
            }
            else
            {
                var previousAuth = await db.Auth.FirstOrDefaultAsync(u => u.UserUID == existingLodestoneAuth.User.UID);
                if (previousAuth != null)
                {
                    db.Auth.Remove(previousAuth);
                }

                var computedHash = StringUtils.Sha256String(StringUtils.GenerateRandomString(64) + DateTime.UtcNow.ToString());
                var auth = new Auth()
                {
                    HashedKey = StringUtils.Sha256String(computedHash),
                    User = existingLodestoneAuth.User,
                };

                embed.WithTitle("Recovery successful");
                embed.WithDescription("This is your new private secret key. Do not share this private secret key with anyone. **If you lose it, it is irrevocably lost.**"
                                      + Environment.NewLine + Environment.NewLine
                                      + $"**{computedHash}**"
                                      + Environment.NewLine + Environment.NewLine
                                      + "Enter this key in Mare Synchronos and hit save to connect to the service.");

                await db.Auth.AddAsync(auth).ConfigureAwait(false);
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        return embed.Build();
    }

    private async Task<Embed> HandleRegisterModalAsync(LodestoneModal arg, ulong userid)
    {
        var embed = new EmbedBuilder();

        var lodestoneId = ParseCharacterIdFromLodestoneUrl(arg.LodestoneUrl);
        if (lodestoneId == null)
        {
            embed.WithTitle("Invalid Lodestone URL");
            embed.WithDescription("The lodestone URL was not valid. It should have following format:" + Environment.NewLine
                + "https://eu.finalfantasyxiv.com/lodestone/character/YOUR_LODESTONE_ID/");
        }
        else
        {
            // check if userid is already in db
            using var scope = _services.CreateScope();

            var hashedLodestoneId = StringUtils.Sha256String(lodestoneId.ToString());

            using var db = scope.ServiceProvider.GetService<MareDbContext>();

            // check if discord id or lodestone id is banned
            if (db.BannedRegistrations.Any(a => a.DiscordIdOrLodestoneAuth == userid.ToString() || a.DiscordIdOrLodestoneAuth == hashedLodestoneId))
            {
                embed.WithTitle("no");
                embed.WithDescription("your account is banned");
            }
            else if (db.LodeStoneAuth.Any(a => a.DiscordId == userid))
            {
                // user already in db
                embed.WithTitle("Registration failed");
                embed.WithDescription("You cannot register more than one lodestone character to your discord account.");
            }
            else if (db.LodeStoneAuth.Any(a => a.HashedLodestoneId == hashedLodestoneId))
            {
                // character already in db
                embed.WithTitle("Registration failed");
                embed.WithDescription("This lodestone character already exists in the Database. If you want to attach this character to your current Discord account use **/relink**.");
            }
            else
            {
                string lodestoneAuth = await GenerateLodestoneAuth(userid, hashedLodestoneId, db).ConfigureAwait(false);
                // check if lodestone id is already in db
                embed.WithTitle("Authorize your character");
                embed.WithDescription("Add following key to your character profile at https://na.finalfantasyxiv.com/lodestone/my/setting/profile/"
                                      + Environment.NewLine + Environment.NewLine
                                      + $"**{lodestoneAuth}**"
                                      + Environment.NewLine + Environment.NewLine
                                      + $"**! THIS IS NOT THE KEY YOU HAVE TO ENTER IN MARE !**"
                                      + Environment.NewLine + Environment.NewLine
                                      + "Once added and saved, use command **/verify** to finish registration and receive a secret key to use for Mare Synchronos."
                                      + Environment.NewLine
                                      + "__You can delete the entry from your profile after verification.__"
                                      + Environment.NewLine + Environment.NewLine
                                      + "The verification will expire in approximately 15 minutes. If you fail to **/verify** the registration will be invalidated and you have to **/register** again.");
                _botServices.DiscordLodestoneMapping[userid] = lodestoneId.ToString();
            }
        }

        return embed.Build();
    }

    private async Task<Embed> HandleRelinkModalAsync(LodestoneModal arg, ulong userid)
    {
        var embed = new EmbedBuilder();

        var lodestoneId = ParseCharacterIdFromLodestoneUrl(arg.LodestoneUrl);
        if (lodestoneId == null)
        {
            embed.WithTitle("Invalid Lodestone URL");
            embed.WithDescription("The lodestone URL was not valid. It should have following format:" + Environment.NewLine
                + "https://eu.finalfantasyxiv.com/lodestone/character/YOUR_LODESTONE_ID/");
        }
        else
        {
            // check if userid is already in db
            using var scope = _services.CreateScope();

            var hashedLodestoneId = StringUtils.Sha256String(lodestoneId.ToString());

            using var db = scope.ServiceProvider.GetService<MareDbContext>();

            // check if discord id or lodestone id is banned
            if (db.BannedRegistrations.Any(a => a.DiscordIdOrLodestoneAuth == userid.ToString() || a.DiscordIdOrLodestoneAuth == hashedLodestoneId))
            {
                embed.WithTitle("no");
                embed.WithDescription("your account is banned");
            }
            else if (db.LodeStoneAuth.Any(a => a.DiscordId == userid))
            {
                // user already in db
                embed.WithTitle("Relink failed");
                embed.WithDescription("You cannot register more than one lodestone character to your discord account.");
            }
            else if (!db.LodeStoneAuth.Any(a => a.HashedLodestoneId == hashedLodestoneId))
            {
                // character already in db
                embed.WithTitle("Relink failed");
                embed.WithDescription("This lodestone character does not exist in the database.");
            }
            else
            {
                string lodestoneAuth = await GenerateLodestoneAuth(userid, hashedLodestoneId, db).ConfigureAwait(false);
                // check if lodestone id is already in db
                embed.WithTitle("Authorize your character for relinking");
                embed.WithDescription("Add following key to your character profile at https://na.finalfantasyxiv.com/lodestone/my/setting/profile/"
                                      + Environment.NewLine + Environment.NewLine
                                      + $"**{lodestoneAuth}**"
                                      + Environment.NewLine + Environment.NewLine
                                      + $"**! THIS IS NOT THE KEY YOU HAVE TO ENTER IN MARE !**"
                                      + Environment.NewLine + Environment.NewLine
                                      + "Once added and saved, use command **/verify_relink** to finish relink and receive a new secret key to use for Mare Synchronos."
                                      + Environment.NewLine
                                      + "__You can delete the entry from your profile after verification.__"
                                      + Environment.NewLine + Environment.NewLine
                                      + "The verification will expire in approximately 15 minutes. If you fail to **/verify_relink** the relink will be invalidated and you have to **/relink** again.");
                _botServices.DiscordRelinkLodestoneMapping[userid] = lodestoneId.ToString();
            }
        }

        return embed.Build();
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

        return auth;
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

    private async Task<EmbedBuilder> HandleVanityUid(EmbedBuilder eb, ulong id, string newUid)
    {
        if (_botServices.LastVanityChange.TryGetValue(id, out var lastChange))
        {
            var timeRemaining = DateTime.UtcNow.Subtract(lastChange);
            if (timeRemaining.TotalHours < 24)
            {
                eb.WithTitle(("Failed to set Vanity UID"));
                eb.WithDescription(
                    $"You can only change your vanity UID once every 24h. Your last change is {timeRemaining} ago.");
            }
        }

        Regex rgx = new(@"^[_\-a-zA-Z0-9]{5,15}$", RegexOptions.ECMAScript);
        if (!rgx.Match(newUid).Success || newUid.Length < 5 || newUid.Length > 15)
        {
            eb.WithTitle("Failed to set Vanity UID");
            eb.WithDescription("The Vanity UID must be between 5 and 15 characters and only contain letters A-Z, numbers 0-9, as well as - and _.");
            return eb;
        }

        using var scope = _services.CreateScope();
        await using var db = scope.ServiceProvider.GetRequiredService<MareDbContext>();

        var lodestoneUser = await db.LodeStoneAuth.Include("User").SingleOrDefaultAsync(u => u.DiscordId == id).ConfigureAwait(false);
        if (lodestoneUser == null)
        {
            eb.WithTitle("Failed to set Vanity UID");
            eb.WithDescription("You do not have a registered account on this server.");
            return eb;
        }

        var uidExists = await db.Users.AnyAsync(u => u.UID == newUid || u.Alias == newUid).ConfigureAwait(false);
        if (uidExists)
        {
            eb.WithTitle("Failed to set Vanity UID");
            eb.WithDescription("This UID is already taken.");
            return eb;
        }

        var user = lodestoneUser.User;
        user.Alias = newUid;
        db.Update(user);
        await db.SaveChangesAsync();

        _botServices.LastVanityChange[id] = DateTime.UtcNow;

        eb.WithTitle("Vanity UID set");
        eb.WithDescription("Your Vanity UID was set to **" + newUid + "**." + Environment.NewLine + "For those changes to apply you will have to reconnect to Mare.");
        return eb;
    }

    private async Task<EmbedBuilder> HandleVanityGid(EmbedBuilder eb, ulong id, string oldGid, string newGid)
    {
        if (_botServices.LastVanityGidChange.TryGetValue(oldGid, out var lastChange))
        {
            var dateTimeDiff = DateTime.UtcNow.Subtract(lastChange);
            if (dateTimeDiff.TotalHours < 24)
            {
                eb.WithTitle(("Failed to set Vanity Syncshell Id"));
                eb.WithDescription(
                    $"You can only change the Vanity Syncshell Id once every 24h. Your last change is {dateTimeDiff} ago.");
            }
        }

        Regex rgx = new(@"^[_\-a-zA-Z0-9]{5,20}$", RegexOptions.ECMAScript);
        if (!rgx.Match(newGid).Success || newGid.Length < 5 || newGid.Length > 20)
        {
            eb.WithTitle("Failed to set Vanity Syncshell Id");
            eb.WithDescription("The Vanity Syncshell Id must be between 5 and 20 characters and only contain letters A-Z, numbers 0-9 as well as - and _.");
            return eb;
        }

        using var scope = _services.CreateScope();
        await using var db = scope.ServiceProvider.GetRequiredService<MareDbContext>();

        var lodestoneUser = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.DiscordId == id).ConfigureAwait(false);
        if (lodestoneUser == null)
        {
            eb.WithTitle("Failed to set Vanity Syncshell Id");
            eb.WithDescription("You do not have a registered account on this server.");
            return eb;
        }

        var group = await db.Groups.FirstOrDefaultAsync(g => g.GID == oldGid || g.Alias == oldGid).ConfigureAwait(false);
        if (group == null)
        {
            eb.WithTitle("Failed to set Vanity Syncshell Id");
            eb.WithDescription("The provided Syncshell Id does not exist.");
            return eb;
        }

        if (lodestoneUser.User.UID != group.OwnerUID)
        {
            eb.WithTitle("Failed to set Vanity Syncshell Id");
            eb.WithDescription("You are not the owner of this Syncshell");
            return eb;
        }

        var uidExists = await db.Groups.AnyAsync(u => u.GID == newGid || u.Alias == newGid).ConfigureAwait(false);
        if (uidExists)
        {
            eb.WithTitle("Failed to set Vanity Syncshell Id");
            eb.WithDescription("This Syncshell Id is already taken.");
            return eb;
        }

        group.Alias = newGid;
        db.Update(group);
        await db.SaveChangesAsync();

        _botServices.LastVanityGidChange[newGid] = DateTime.UtcNow;
        _botServices.LastVanityGidChange[oldGid] = DateTime.UtcNow;

        eb.WithTitle("Vanity Syncshell Id set");
        eb.WithDescription("The Vanity Syncshell Id was set to **" + newGid + "**." + Environment.NewLine + "For those changes to apply you will have to reconnect to Mare.");
        return eb;
    }

    private async Task DeletePreviousUserAccount(ulong id)
    {
        using var scope = _services.CreateScope();
        using var db = scope.ServiceProvider.GetService<MareDbContext>();
        var discordAuthedUser = await db.LodeStoneAuth.Include(u => u.User).FirstOrDefaultAsync(u => u.DiscordId == id).ConfigureAwait(false);
        if (discordAuthedUser != null)
        {
            if (discordAuthedUser.User != null)
            {
                var maxGroupsByUser = _mareClientConfigurationService.GetValueOrDefault(nameof(ServerConfiguration.MaxGroupUserCount), 3);

                await SharedDbFunctions.PurgeUser(_logger, discordAuthedUser.User, db, maxGroupsByUser);
            }
            else
            {
                db.Remove(discordAuthedUser);
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleVerifyRelinkAsync(SocketSlashCommand cmd, IServiceProvider serviceProvider)
    {
        var embedBuilder = new EmbedBuilder();

        using var scope = serviceProvider.CreateScope();
        var req = new HttpClient();
        using var db = scope.ServiceProvider.GetService<MareDbContext>();

        var lodestoneAuth = db.LodeStoneAuth.SingleOrDefault(u => u.DiscordId == cmd.User.Id);
        if (lodestoneAuth != null && _botServices.DiscordRelinkLodestoneMapping.ContainsKey(cmd.User.Id))
        {
            var randomServer = _botServices.LodestoneServers[random.Next(_botServices.LodestoneServers.Length)];
            var response = await req.GetAsync($"https://{randomServer}.finalfantasyxiv.com/lodestone/character/{_botServices.DiscordRelinkLodestoneMapping[cmd.User.Id]}").ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (content.Contains(lodestoneAuth.LodestoneAuthString))
                {
                    _botServices.DiscordRelinkLodestoneMapping.TryRemove(cmd.User.Id, out _);

                    var existingLodestoneAuth = db.LodeStoneAuth.Include(u => u.User).SingleOrDefault(u => u.DiscordId != cmd.User.Id && u.HashedLodestoneId == lodestoneAuth.HashedLodestoneId);

                    var previousAuth = await db.Auth.FirstOrDefaultAsync(u => u.UserUID == existingLodestoneAuth.User.UID);
                    if (previousAuth != null)
                    {
                        db.Auth.Remove(previousAuth);
                    }

                    var computedHash = StringUtils.Sha256String(StringUtils.GenerateRandomString(64) + DateTime.UtcNow.ToString());
                    var auth = new Auth()
                    {
                        HashedKey = StringUtils.Sha256String(computedHash),
                        User = existingLodestoneAuth.User,
                    };

                    lodestoneAuth.StartedAt = null;
                    lodestoneAuth.LodestoneAuthString = null;
                    lodestoneAuth.User = existingLodestoneAuth.User;

                    db.LodeStoneAuth.Remove(existingLodestoneAuth);

                    await db.Auth.AddAsync(auth).ConfigureAwait(false);

                    _botServices.Logger.LogInformation("User relinked: {userUID}", lodestoneAuth.User.UID);

                    embedBuilder.WithTitle("Relink successful");
                    embedBuilder.WithDescription("This is your **new** private secret key. Do not share this private secret key with anyone. **If you lose it, it is irrevocably lost.**"
                                                 + Environment.NewLine + Environment.NewLine
                                                 + $"**{computedHash}**"
                                                 + Environment.NewLine + Environment.NewLine
                                                 + "Enter this key in Mare Synchronos and hit save to connect to the service.");
                }
                else
                {
                    embedBuilder.WithTitle("Failed to verify your character");
                    embedBuilder.WithDescription("Did not find requested authentication key on your profile. Make sure you have saved *twice*, then do **/relink_verify** again.");
                    lodestoneAuth.StartedAt = DateTime.UtcNow;
                }
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        else
        {
            embedBuilder.WithTitle("Your auth has expired or something else went wrong");
            embedBuilder.WithDescription("Start again with **/relink**");
            _botServices.DiscordRelinkLodestoneMapping.TryRemove(cmd.User.Id, out _);
        }

        var dataEmbed = embedBuilder.Build();

        await cmd.FollowupAsync(embed: dataEmbed, ephemeral: true).ConfigureAwait(false);
    }

    private async Task HandleVerifyAsync(SocketSlashCommand cmd, IServiceProvider serviceProvider)
    {
        var embedBuilder = new EmbedBuilder();

        using var scope = serviceProvider.CreateScope();
        var req = new HttpClient();
        using var db = scope.ServiceProvider.GetService<MareDbContext>();

        var lodestoneAuth = db.LodeStoneAuth.SingleOrDefault(u => u.DiscordId == cmd.User.Id);
        if (lodestoneAuth != null && _botServices.DiscordLodestoneMapping.ContainsKey(cmd.User.Id))
        {
            var randomServer = _botServices.LodestoneServers[random.Next(_botServices.LodestoneServers.Length)];
            var response = await req.GetAsync($"https://{randomServer}.finalfantasyxiv.com/lodestone/character/{_botServices.DiscordLodestoneMapping[cmd.User.Id]}").ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (content.Contains(lodestoneAuth.LodestoneAuthString))
                {
                    _botServices.DiscordLodestoneMapping.TryRemove(cmd.User.Id, out _);

                    var user = new User();

                    var hasValidUid = false;
                    while (!hasValidUid)
                    {
                        var uid = StringUtils.GenerateRandomString(10);
                        if (db.Users.Any(u => u.UID == uid || u.Alias == uid)) continue;
                        user.UID = uid;
                        hasValidUid = true;
                    }

                    // make the first registered user on the service to admin
                    if (!await db.Users.AnyAsync().ConfigureAwait(false))
                    {
                        user.IsAdmin = true;
                    }

                    user.LastLoggedIn = DateTime.UtcNow;

                    var computedHash = StringUtils.Sha256String(StringUtils.GenerateRandomString(64) + DateTime.UtcNow.ToString());
                    var auth = new Auth()
                    {
                        HashedKey = StringUtils.Sha256String(computedHash),
                        User = user,
                    };

                    await db.Users.AddAsync(user).ConfigureAwait(false);
                    await db.Auth.AddAsync(auth).ConfigureAwait(false);

                    _botServices.Logger.LogInformation("User registered: {userUID}", user.UID);

                    _botServices.Metrics.IncGauge(MetricsAPI.GaugeUsersRegistered, 1);

                    lodestoneAuth.StartedAt = null;
                    lodestoneAuth.User = user;
                    lodestoneAuth.LodestoneAuthString = null;

                    embedBuilder.WithTitle("Registration successful");
                    embedBuilder.WithDescription("This is your private secret key. Do not share this private secret key with anyone. **If you lose it, it is irrevocably lost.**"
                                                 + Environment.NewLine + Environment.NewLine
                                                 + $"**{computedHash}**"
                                                 + Environment.NewLine + Environment.NewLine
                                                 + "Enter this key in Mare Synchronos and hit save to connect to the service."
                                                 + Environment.NewLine
                                                 + "You should connect as soon as possible to not get caught by the automatic cleanup process."
                                                 + Environment.NewLine
                                                 + "Have fun.");
                }
                else
                {
                    embedBuilder.WithTitle("Failed to verify your character");
                    embedBuilder.WithDescription("Did not find requested authentication key on your profile. Make sure you have saved *twice*, then do **/verify** again.");
                    lodestoneAuth.StartedAt = DateTime.UtcNow;
                }
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        else
        {
            embedBuilder.WithTitle("Your auth has expired or something else went wrong");
            embedBuilder.WithDescription("Start again with **/register**");
            _botServices.DiscordLodestoneMapping.TryRemove(cmd.User.Id, out _);
        }

        var dataEmbed = embedBuilder.Build();

        await cmd.FollowupAsync(embed: dataEmbed, ephemeral: true).ConfigureAwait(false);
    }
}
