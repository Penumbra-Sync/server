using Discord;
using Discord.Interactions;
using MareSynchronosShared.Data;
using System.Text.RegularExpressions;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Discord.WebSocket;
using System.Linq;
using Prometheus;
using MareSynchronosServices.Authentication;
using MareSynchronosShared.Models;
using System.Text;
using System.Security.Cryptography;
using MareSynchronosServices.Identity;

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
    private readonly IServiceProvider _services;
    private readonly DiscordBotServices _botServices;
    private readonly IdentityHandler _identityHandler;
    private readonly CleanupService _cleanupService;

    public MareModule(IServiceProvider services, DiscordBotServices botServices, IdentityHandler identityHandler, CleanupService cleanupService)
    {
        _services = services;
        _botServices = botServices;
        _identityHandler = identityHandler;
        _cleanupService = cleanupService;
    }

    [SlashCommand("register", "Starts the registration process for the Mare Synchronos server of this Discord")]
    public async Task Register([Summary("overwrite", "Overwrites your old account")] bool overwrite = false)
    {
        if (overwrite)
        {
            await DeletePreviousUserAccount(Context.User.Id).ConfigureAwait(false);
        }

        await RespondWithModalAsync<LodestoneModal>("register_modal").ConfigureAwait(false);
    }

    [SlashCommand("setvanityuid", "Sets your Vanity UID.")]
    public async Task SetVanityUid([Summary("vanity_uid", "Desired Vanity UID")] string vanityUid)
    {
        EmbedBuilder eb = new();

        eb = await HandleVanityUid(eb, Context.User.Id, vanityUid);

        await RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
    }

    [SlashCommand("setsyncshellvanityid", "Sets a Vanity GID for a Syncshell")]
    public async Task SetSyncshellVanityId(
        [Summary("syncshell_id", "Syncshell ID")] string syncshellId,
        [Summary("vanity_syncshell_id", "Desired Vanity Syncshell ID")] string vanityId)
    {
        EmbedBuilder eb = new();

        eb = await HandleVanityGid(eb, Context.User.Id, syncshellId, vanityId);

        await RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
    }

    [SlashCommand("verify", "Finishes the registration process for the Mare Synchronos server of this Discord")]
    public async Task Verify()
    {
        EmbedBuilder eb = new();
        if (_botServices.verificationQueue.Any(u => u.User.Id == Context.User.Id))
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
            _botServices.verificationQueue.Enqueue((SocketSlashCommand)Context.Interaction);
        }
    }

    [SlashCommand("recover", "Allows you to recover your account by generating a new secret key")]
    public async Task Recover()
    {
        await RespondWithModalAsync<LodestoneModal>("recover_modal").ConfigureAwait(false);
    }

    [SlashCommand("userinfo", "Shows you your user information")]
    public async Task UserInfo(
        [Summary("discord_user", "ADMIN ONLY: Discord User to check for")] IUser? discordUser = null,
        [Summary("uid", "ADMIN ONLY: UID to check for")] string? uid = null)
    {
        EmbedBuilder eb = new();

        eb = await HandleUserInfo(eb, Context.User.Id, discordUser?.Id ?? null, uid);

        await RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
    }

    [ModalInteraction("recover_modal")]
    public async Task RecoverModal(LodestoneModal modal)
    {
        var embed = await HandleRecoverModalAsync(modal, Context.User.Id).ConfigureAwait(false);
        await RespondAsync(embeds: new Embed[] { embed }, ephemeral: true).ConfigureAwait(false);
    }

    [ModalInteraction("register_modal")]
    public async Task RegisterModal(LodestoneModal modal)
    {
        var embed = await HandleRegisterModalAsync(modal, Context.User.Id).ConfigureAwait(false);
        await RespondAsync(embeds: new Embed[] { embed }, ephemeral: true).ConfigureAwait(false);
    }

    private async Task<EmbedBuilder> HandleUserInfo(EmbedBuilder eb, ulong id, ulong? optionalUser, string? uid)
    {
        using var scope = _services.CreateScope();
        await using var db = scope.ServiceProvider.GetRequiredService<MareDbContext>();

        var self = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.DiscordId == id).ConfigureAwait(false);
        ulong userToCheckForDiscordId = id;
        bool isAdminCall = self.User.IsModerator || self.User.IsAdmin;

        if (self == null)
        {
            eb.WithTitle("No account");
            eb.WithDescription("No Mare account was found associated to your Discord user");
            return eb;
        }

        if ((optionalUser != null || uid != null) && !isAdminCall)
        {
            eb.WithTitle("Unauthorized");
            eb.WithDescription("You are not authorized to view another users' information");
            return eb;
        }
        else
        {
            LodeStoneAuth userInDb = null;
            if (optionalUser != null)
            {
                userInDb = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.DiscordId == optionalUser).ConfigureAwait(false);
            }
            else if (uid != null)
            {
                userInDb = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.User.UID == uid).ConfigureAwait(false);
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
        var identity = await _identityHandler.GetIdentForuid(dbUser.UID).ConfigureAwait(false);
        var groups = await db.Groups.Where(g => g.OwnerUID == dbUser.UID).ToListAsync().ConfigureAwait(false);
        var groupsJoined = await db.GroupPairs.Where(g => g.GroupUserUID == dbUser.UID).ToListAsync().ConfigureAwait(false);

        eb.WithTitle("User Information");
        eb.WithDescription("This is the user information for Discord User Id " + userToCheckForDiscordId + Environment.NewLine
            + "If you want to verify your secret key is valid, go to https://emn178.github.io/online-tools/sha256.html and copy your secret key into there and compare it to the Hashed Secret Key.");
        eb.AddField("UID", dbUser.UID);
        eb.AddField("Vanity UID", dbUser.Alias);
        eb.AddField("Last Online (UTC)", dbUser.LastLoggedIn.ToString("U"));
        eb.AddField("Currently online: ", !string.IsNullOrEmpty(identity.CharacterIdent));
        eb.AddField("Hashed Secret Key", auth.HashedKey);
        eb.AddField("Joined Syncshells", groupsJoined.Count);
        eb.AddField("Owned Syncshells", groups.Count);
        foreach (var group in groups)
        {
            var syncShellUserCount = await db.GroupPairs.CountAsync(g => g.GroupGID == group.GID).ConfigureAwait(false);
            eb.AddField("Owned Syncshell " + group.GID + " Vanity ID", group.Alias);
            eb.AddField("Owned Syncshell " + group.GID + " User Count", syncShellUserCount);
        }

        if (isAdminCall)
        {
            eb.AddField("Character Ident", identity.CharacterIdent);
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
            using var sha256 = SHA256.Create();

            var hashedLodestoneId = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(lodestoneId.ToString()))).Replace("-", "");

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

                var computedHash = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(DiscordBotServices.GenerateRandomString(64) + DateTime.UtcNow.ToString()))).Replace("-", "");
                var auth = new Auth()
                {
                    HashedKey = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(computedHash)))
                        .Replace("-", ""),
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

                var authHandler = scope.ServiceProvider.GetService<SecretKeyAuthenticationHandler>();
                authHandler.RemoveAuthentication(existingLodestoneAuth.User.UID);
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
            using var sha256 = SHA256.Create();

            var hashedLodestoneId = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(lodestoneId.ToString()))).Replace("-", "");

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
                embed.WithDescription("This lodestone character already exists in the Database. If you are the rightful owner for this character and lost your secret key generated with it, contact the developer.");
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
                                      + "You can delete the entry from your profile after verification."
                                      + Environment.NewLine + Environment.NewLine
                                      + "The verification will expire in approximately 15 minutes. If you fail to **/verify** the registration will be invalidated and you have to **/register** again.");
                _botServices.DiscordLodestoneMapping[userid] = lodestoneId.ToString();
            }
        }

        return embed.Build();
    }

    private async Task<string> GenerateLodestoneAuth(ulong discordid, string hashedLodestoneId, MareDbContext dbContext)
    {
        var auth = DiscordBotServices.GenerateRandomString(32);
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
                await _cleanupService.PurgeUser(discordAuthedUser.User, db);
            }
            else
            {
                db.Remove(discordAuthedUser);
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }
    }
}
