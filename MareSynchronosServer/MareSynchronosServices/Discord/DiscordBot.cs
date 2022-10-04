using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using MareSynchronosServices.Authentication;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServices.Discord;

public class DiscordBot : IHostedService
{
    private readonly CleanupService cleanupService;
    private readonly MareMetrics metrics;
    private readonly IClientIdentificationService clientService;
    private readonly IServiceProvider services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DiscordBot> logger;
    private readonly Random random;
    private string authToken = string.Empty;
    DiscordSocketClient discordClient;
    ConcurrentDictionary<ulong, string> DiscordLodestoneMapping = new();
    private CancellationTokenSource? verificationTaskCts;
    private CancellationTokenSource? updateStatusCts;
    private CancellationTokenSource? vanityUpdateCts;
    private readonly string[] LodestoneServers = new[] { "eu", "na", "jp", "fr", "de" };
    private readonly ConcurrentQueue<SocketSlashCommand> verificationQueue = new();
    private ConcurrentDictionary<ulong, DateTime> LastVanityChange = new();
    private ConcurrentDictionary<string, DateTime> LastVanityGidChange = new();
    private ulong vanityCommandId;
    private ulong vanityGidCommandId;
    private Task cleanUpUserTask = null;

    private SemaphoreSlim semaphore;

    public DiscordBot(CleanupService cleanupService, MareMetrics metrics, IClientIdentificationService clientService, IServiceProvider services, IConfiguration configuration, ILogger<DiscordBot> logger)
    {
        this.cleanupService = cleanupService;
        this.metrics = metrics;
        this.clientService = clientService;
        this.services = services;
        _configuration = configuration.GetRequiredSection("MareSynchronos");
        this.logger = logger;
        this.verificationQueue = new ConcurrentQueue<SocketSlashCommand>();
        this.semaphore = new SemaphoreSlim(1);

        random = new();
        authToken = _configuration.GetValue<string>("DiscordBotToken");

        discordClient = new(new DiscordSocketConfig()
        {
            DefaultRetryMode = RetryMode.AlwaysRetry
        });

        discordClient.Log += Log;
    }

    private async Task DiscordClient_SlashCommandExecuted(SocketSlashCommand arg)
    {
        await semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            switch (arg.Data.Name)
            {
                case "register":
                    {
                        if (arg.Data.Options.FirstOrDefault(f => f.Name == "overwrite_old_account") != null)
                        {
                            await DeletePreviousUserAccount(arg.User.Id).ConfigureAwait(false);
                        }

                        var modal = new ModalBuilder();
                        modal.WithTitle("Verify with Lodestone");
                        modal.WithCustomId("register_modal");
                        modal.AddTextInput("Enter the Lodestone URL of your Character", "lodestoneurl", TextInputStyle.Short, "https://*.finalfantasyxiv.com/lodestone/character/<CHARACTERID>/", required: true);
                        await arg.RespondWithModalAsync(modal.Build()).ConfigureAwait(false);
                        break;
                    }
                case "recover":
                    {
                        var modal = new ModalBuilder();
                        modal.WithTitle("Verify with Lodestone");
                        modal.WithCustomId("recover_modal");
                        modal.AddTextInput("Enter the Lodestone URL of your Character", "lodestoneurl", TextInputStyle.Short, "https://*.finalfantasyxiv.com/lodestone/character/<CHARACTERID>/", required: true);
                        await arg.RespondWithModalAsync(modal.Build()).ConfigureAwait(false);
                        break;
                    }
                case "verify":
                    {
                        EmbedBuilder eb = new();
                        if (verificationQueue.Any(u => u.User.Id == arg.User.Id))
                        {
                            eb.WithTitle("Already queued for verfication");
                            eb.WithDescription("You are already queued for verification. Please wait.");
                            await arg.RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
                        }
                        else if (!DiscordLodestoneMapping.ContainsKey(arg.User.Id))
                        {
                            eb.WithTitle("Cannot verify registration");
                            eb.WithDescription("You need to **/register** first before you can **/verify**");
                            await arg.RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
                        }
                        else
                        {
                            await arg.DeferAsync(ephemeral: true).ConfigureAwait(false);
                            verificationQueue.Enqueue(arg);
                        }

                        break;
                    }
                case "setvanityuid":
                    {
                        EmbedBuilder eb = new();
                        var newUid = (string)arg.Data.Options.First(f => f.Name == "vanity_uid").Value;
                        eb = await HandleVanityUid(eb, arg.User.Id, newUid);

                        await arg.RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
                        break;
                    }
                case "setsyncshellvanityid":
                    {
                        EmbedBuilder eb = new();
                        var oldGid = (string)arg.Data.Options.First(f => f.Name == "syncshell_id").Value;
                        var newGid = (string)arg.Data.Options.First(f => f.Name == "vanity_syncshell_id").Value;

                        eb = await HandleVanityGid(eb, arg.User.Id, oldGid, newGid);

                        await arg.RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);

                        break;
                    }
                default:
                    await arg.RespondAsync("idk what you did to get here to start, just follow the instructions as provided.", ephemeral: true).ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<EmbedBuilder> HandleVanityGid(EmbedBuilder eb, ulong id, string oldGid, string newGid)
    {
        if (LastVanityGidChange.TryGetValue(oldGid, out var lastChange))
        {
            var dateTimeDiff = DateTime.UtcNow.Subtract(lastChange);
            if (dateTimeDiff.TotalHours < 24)
            {
                eb.WithTitle(("Failed to set Vanity Syncshell Id"));
                eb.WithDescription(
                    $"You can only change the Vanity Syncshell Id once every 24h. Your last change is {dateTimeDiff} ago.");
            }
        }

        Regex rgx = new(@"[_\-a-zA-Z0-9]{5,20}", RegexOptions.ECMAScript);
        if (!rgx.Match(newGid).Success || newGid.Length < 5 || newGid.Length > 20)
        {
            eb.WithTitle("Failed to set Vanity Syncshell Id");
            eb.WithDescription("The Vanity Syncshell Id must be between 5 and 20 characters and only contain letters A-Z, numbers 0-9 as well as - and _.");
            return eb;
        }

        using var scope = services.CreateScope();
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

        LastVanityGidChange[newGid] = DateTime.UtcNow;
        LastVanityGidChange[oldGid] = DateTime.UtcNow;

        eb.WithTitle("Vanity Syncshell Id set");
        eb.WithDescription("The Vanity Syncshell Id was set to **" + newGid + "**." + Environment.NewLine + "For those changes to apply you will have to reconnect to Mare.");
        return eb;
    }

    private async Task<EmbedBuilder> HandleVanityUid(EmbedBuilder eb, ulong id, string newUid)
    {
        if (LastVanityChange.TryGetValue(id, out var lastChange))
        {
            var timeRemaining = DateTime.UtcNow.Subtract(lastChange);
            if (timeRemaining.TotalHours < 24)
            {
                eb.WithTitle(("Failed to set Vanity UID"));
                eb.WithDescription(
                    $"You can only change your vanity UID once every 24h. Your last change is {timeRemaining} ago.");
            }
        }

        Regex rgx = new(@"[_\-a-zA-Z0-9]{5,15}", RegexOptions.ECMAScript);
        if (!rgx.Match(newUid).Success || newUid.Length < 5 || newUid.Length > 15)
        {
            eb.WithTitle("Failed to set Vanity UID");
            eb.WithDescription("The Vanity UID must be between 5 and 20 characters and only contain letters A-Z, numbers 0-9, as well as - and _.");
            return eb;
        }

        using var scope = services.CreateScope();
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

        LastVanityChange[id] = DateTime.UtcNow;

        eb.WithTitle("Vanity UID set");
        eb.WithDescription("Your Vanity UID was set to **" + newUid + "**." + Environment.NewLine + "For those changes to apply you will have to reconnect to Mare.");
        return eb;
    }

    private async Task DeletePreviousUserAccount(ulong id)
    {
        using var scope = services.CreateScope();
        using var db = scope.ServiceProvider.GetService<MareDbContext>();
        var discordAuthedUser = await db.LodeStoneAuth.Include(u => u.User).FirstOrDefaultAsync(u => u.DiscordId == id).ConfigureAwait(false);
        if (discordAuthedUser != null)
        {
            if (discordAuthedUser.User != null)
            {
                await cleanupService.PurgeUser(discordAuthedUser.User, db);
            }
            else
            {
                db.Remove(discordAuthedUser);
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    private async Task DiscordClient_ModalSubmitted(SocketModal arg)
    {
        switch (arg.Data.CustomId)
        {
            case "register_modal":
                {
                    var embed = await HandleRegisterModalAsync(arg).ConfigureAwait(false);
                    await arg.RespondAsync(embeds: new Embed[] { embed }, ephemeral: true).ConfigureAwait(false);
                    break;
                }
            case "recover_modal":
                {
                    var embed = await HandleRecoverModalAsync(arg).ConfigureAwait(false);
                    await arg.RespondAsync(embeds: new Embed[] { embed }, ephemeral: true).ConfigureAwait(false);
                    break;
                }
        }
    }

    private async Task<Embed> HandleVerifyAsync(ulong id)
    {
        var embedBuilder = new EmbedBuilder();

        using var scope = services.CreateScope();
        var req = new HttpClient();
        using var db = scope.ServiceProvider.GetService<MareDbContext>();

        var lodestoneAuth = db.LodeStoneAuth.SingleOrDefault(u => u.DiscordId == id);
        if (lodestoneAuth != null && DiscordLodestoneMapping.ContainsKey(id))
        {
            var randomServer = LodestoneServers[random.Next(LodestoneServers.Length)];
            var response = await req.GetAsync($"https://{randomServer}.finalfantasyxiv.com/lodestone/character/{DiscordLodestoneMapping[id]}").ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (content.Contains(lodestoneAuth.LodestoneAuthString))
                {
                    DiscordLodestoneMapping.TryRemove(id, out _);

                    using var sha256 = SHA256.Create();
                    var user = new User();

                    var hasValidUid = false;
                    while (!hasValidUid)
                    {
                        var uid = GenerateRandomString(10);
                        if (db.Users.Any(u => u.UID == uid || u.Alias == uid)) continue;
                        user.UID = uid;
                        hasValidUid = true;
                    }

                    // make the first registered user on the service to admin
                    if (!await db.Users.AnyAsync().ConfigureAwait(false))
                    {
                        user.IsAdmin = true;
                    }

                    if (_configuration.GetValue<bool>("PurgeUnusedAccounts"))
                    {
                        var purgedDays = _configuration.GetValue<int>("PurgeUnusedAccountsPeriodInDays");
                        user.LastLoggedIn = DateTime.UtcNow - TimeSpan.FromDays(purgedDays) + TimeSpan.FromDays(1);
                    }

                    var computedHash = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(GenerateRandomString(64) + DateTime.UtcNow.ToString()))).Replace("-", "");
                    var auth = new Auth()
                    {
                        HashedKey = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(computedHash)))
                            .Replace("-", ""),
                        User = user,
                    };

                    await db.Users.AddAsync(user).ConfigureAwait(false);
                    await db.Auth.AddAsync(auth).ConfigureAwait(false);

                    logger.LogInformation("User registered: {userUID}", user.UID);

                    metrics.IncGauge(MetricsAPI.GaugeUsersRegistered, 1);

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
            DiscordLodestoneMapping.TryRemove(id, out _);
        }

        return embedBuilder.Build();
    }

    private async Task<Embed> HandleRecoverModalAsync(SocketModal arg)
    {
        var embed = new EmbedBuilder();

        var lodestoneId = ParseCharacterIdFromLodestoneUrl(arg.Data.Components.Single(c => c.CustomId == "lodestoneurl").Value);
        if (lodestoneId == null)
        {
            embed.WithTitle("Invalid Lodestone URL");
            embed.WithDescription("The lodestone URL was not valid. It should have following format:" + Environment.NewLine
                + "https://eu.finalfantasyxiv.com/lodestone/character/YOUR_LODESTONE_ID/");
        }
        else
        {
            using var scope = services.CreateScope();
            using var sha256 = SHA256.Create();

            var hashedLodestoneId = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(lodestoneId.ToString()))).Replace("-", "");

            await using var db = scope.ServiceProvider.GetService<MareDbContext>();
            var existingLodestoneAuth = await db.LodeStoneAuth.Include("User")
                .FirstOrDefaultAsync(a => a.DiscordId == arg.User.Id && a.HashedLodestoneId == hashedLodestoneId)
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

                var computedHash = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(GenerateRandomString(64) + DateTime.UtcNow.ToString()))).Replace("-", "");
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

    private async Task<Embed> HandleRegisterModalAsync(SocketModal arg)
    {
        var embed = new EmbedBuilder();

        var lodestoneId = ParseCharacterIdFromLodestoneUrl(arg.Data.Components.Single(c => c.CustomId == "lodestoneurl").Value);
        if (lodestoneId == null)
        {
            embed.WithTitle("Invalid Lodestone URL");
            embed.WithDescription("The lodestone URL was not valid. It should have following format:" + Environment.NewLine
                + "https://eu.finalfantasyxiv.com/lodestone/character/YOUR_LODESTONE_ID/");
        }
        else
        {
            // check if userid is already in db
            using var scope = services.CreateScope();
            using var sha256 = SHA256.Create();

            var hashedLodestoneId = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(lodestoneId.ToString()))).Replace("-", "");

            using var db = scope.ServiceProvider.GetService<MareDbContext>();

            // check if discord id or lodestone id is banned
            if (db.BannedRegistrations.Any(a => a.DiscordIdOrLodestoneAuth == arg.User.Id.ToString() || a.DiscordIdOrLodestoneAuth == hashedLodestoneId))
            {
                embed.WithTitle("no");
                embed.WithDescription("your account is banned");
            }
            else if (db.LodeStoneAuth.Any(a => a.DiscordId == arg.User.Id))
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
                string lodestoneAuth = await GenerateLodestoneAuth(arg.User.Id, hashedLodestoneId, db).ConfigureAwait(false);
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
                DiscordLodestoneMapping[arg.User.Id] = lodestoneId.ToString();
            }
        }

        return embed.Build();
    }

    private async Task<string> GenerateLodestoneAuth(ulong discordid, string hashedLodestoneId, MareDbContext dbContext)
    {
        var auth = GenerateRandomString(32);
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

    private async Task DiscordClient_Ready()
    {
        var register = new SlashCommandBuilder()
            .WithName("register")
            .WithDescription("Registration for the Mare Synchronos server of this Discord")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("new_account")
                .WithDescription("Starts the registration process for the Mare Synchronos server of this Discord")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("overwrite_old_account")
                .WithDescription("Will forcefully overwrite your current character on the service, if present")
                .WithType(ApplicationCommandOptionType.SubCommand));

        var verify = new SlashCommandBuilder();
        verify.WithName("verify");
        verify.WithDescription("Finishes the registration process for the Mare Synchronos server of this Discord");

        var vanityuid = new SlashCommandBuilder();
        vanityuid.WithName("setvanityuid");
        vanityuid.WithDescription("Sets your Vanity UID.");
        vanityuid.AddOption("vanity_uid", ApplicationCommandOptionType.String, "Desired Vanity UID", isRequired: true);

        var vanitygid = new SlashCommandBuilder();
        vanitygid.WithName("setsyncshellvanityid");
        vanitygid.WithDescription("Sets a Vanity GID for a Syncshell");
        vanitygid.AddOption("syncshell_id", ApplicationCommandOptionType.String, "Syncshell ID", isRequired: true);
        vanitygid.AddOption("vanity_syncshell_id", ApplicationCommandOptionType.String, "Desired Vanity Syncshell ID", isRequired: true);

        var recover = new SlashCommandBuilder();
        recover.WithName("recover");
        recover.WithDescription("Allows you to recover your account by generating a new secret key");

        try
        {
            await discordClient.Rest.DeleteAllGlobalCommandsAsync().ConfigureAwait(false);

            var guild = (await discordClient.Rest.GetGuildsAsync()).First();
            var commands = await guild.GetApplicationCommandsAsync();
            if (!commands.Any(c => c.Name.Contains("register")))
            {
                await guild.CreateApplicationCommandAsync(register.Build()).ConfigureAwait(false);
            }
            if (!commands.Any(c => c.Name.Contains("verify")))
            {
                await guild.CreateApplicationCommandAsync(verify.Build()).ConfigureAwait(false);
            }
            if (!commands.Any(c => c.Name.Contains("setvanityuid")))
            {
                var vanityCommand = await guild.CreateApplicationCommandAsync(vanityuid.Build()).ConfigureAwait(false);
                vanityCommandId = vanityCommand.Id;
            }
            else
            {
                vanityCommandId = commands.First(c => c.Name.Contains("setvanityuid")).Id;
            }
            if (!commands.Any(c => c.Name.Contains("setsyncshellvanityid")))
            {
                await guild.CreateApplicationCommandAsync(vanitygid.Build()).ConfigureAwait(false);
            }
            if (!commands.Any(c => c.Name.Contains("recover")))
            {
                await guild.CreateApplicationCommandAsync(recover.Build()).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create command");
        }

        if (cleanUpUserTask == null)
        {
            cleanUpUserTask = RemoveUsersNotInVanityRole();
        }
    }

    private Task Log(LogMessage msg)
    {
        logger.LogInformation("{msg}", msg);

        return Task.CompletedTask;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(authToken))
        {
            authToken = _configuration.GetValue<string>("DiscordBotToken");

            await discordClient.LoginAsync(TokenType.Bot, authToken).ConfigureAwait(false);
            await discordClient.StartAsync().ConfigureAwait(false);

            discordClient.Ready += DiscordClient_Ready;
            discordClient.SlashCommandExecuted += DiscordClient_SlashCommandExecuted;
            discordClient.ModalSubmitted += DiscordClient_ModalSubmitted;

            _ = ProcessQueueWork();
            _ = UpdateStatusAsync();
        }
    }

    private async Task ProcessQueueWork()
    {
        verificationTaskCts = new CancellationTokenSource();
        while (!verificationTaskCts.IsCancellationRequested)
        {
            if (verificationQueue.TryDequeue(out var queueitem))
            {
                try
                {
                    var dataEmbed = await HandleVerifyAsync(queueitem.User.Id).ConfigureAwait(false);
                    await queueitem.FollowupAsync(embed: dataEmbed, ephemeral: true).ConfigureAwait(false);

                    logger.LogInformation("Sent login information to user");
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error during queue work");
                }

            }
            await Task.Delay(TimeSpan.FromSeconds(2), verificationTaskCts.Token).ConfigureAwait(false);
        }
    }

    private async Task RemoveUsersNotInVanityRole()
    {
        vanityUpdateCts = new();
        while (!vanityUpdateCts.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation($"Cleaning up Vanity UIDs");
                var guild = discordClient.Guilds.FirstOrDefault();
                if (guild == null)
                {
                    logger.LogInformation($"Guild was null");
                    throw new Exception("Guild is null");
                }

                logger.LogInformation("Getting application commands from guild {guildName}", guild.Name);
                var restGuild = await discordClient.Rest.GetGuildAsync(guild.Id);
                var vanityCommand = await restGuild.GetSlashCommandAsync(vanityCommandId).ConfigureAwait(false);
                GuildApplicationCommandPermission commandPermissions = null;
                try
                {
                    logger.LogInformation($"Getting command permissions");
                    commandPermissions = await vanityCommand.GetCommandPermission().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error getting command permissions");
                    throw new Exception("Can't get command permissions");
                }

                logger.LogInformation($"Getting allowed role ids from permissions");
                List<ulong> allowedRoleIds = new();
                try
                {
                    allowedRoleIds = (from perm in commandPermissions.Permissions where perm.TargetType == ApplicationCommandPermissionTarget.Role where perm.Permission select perm.TargetId).ToList();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error resolving permissions to roles");
                }

                logger.LogInformation($"Found allowed role ids: {string.Join(", ", allowedRoleIds)}");

                if (allowedRoleIds.Any())
                {
                    await using var scope = services.CreateAsyncScope();
                    await using (var db = scope.ServiceProvider.GetRequiredService<MareDbContext>())
                    {
                        var aliasedUsers = db.LodeStoneAuth.Include("User")
                            .Where(c => c.User != null && !string.IsNullOrEmpty(c.User.Alias));
                        var aliasedGroups = db.Groups.Include(u => u.Owner)
                            .Where(c => !string.IsNullOrEmpty(c.Alias));

                        foreach (var lodestoneAuth in aliasedUsers)
                        {
                            var discordUser = await restGuild.GetUserAsync(lodestoneAuth.DiscordId).ConfigureAwait(false);
                            logger.LogInformation($"Checking User: {lodestoneAuth.DiscordId}, {lodestoneAuth.User.UID} ({lodestoneAuth.User.Alias}), User in Roles: {string.Join(", ", discordUser?.RoleIds ?? new List<ulong>())}");

                            if (discordUser == null || !discordUser.RoleIds.Any(u => allowedRoleIds.Contains(u)))
                            {
                                logger.LogInformation($"User {lodestoneAuth.User.UID} not in allowed roles, deleting alias");
                                lodestoneAuth.User.Alias = string.Empty;
                                db.Update(lodestoneAuth.User);
                            }

                            await Task.Delay(100);
                            await db.SaveChangesAsync().ConfigureAwait(false);
                        }

                        foreach (var group in aliasedGroups)
                        {
                            var lodestoneUser = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(f => f.User.UID == group.OwnerUID);
                            RestGuildUser discordUser = null;
                            if (lodestoneUser != null)
                            {
                                discordUser = await restGuild.GetUserAsync(lodestoneUser.DiscordId).ConfigureAwait(false);
                            }

                            logger.LogInformation($"Checking Group: {group.GID}, owned by {lodestoneUser?.User?.UID ?? string.Empty} ({lodestoneUser?.User?.Alias ?? string.Empty}), User in Roles: {string.Join(", ", discordUser?.RoleIds ?? new List<ulong>())}");

                            if (lodestoneUser == null || discordUser == null || !discordUser.RoleIds.Any(u => allowedRoleIds.Contains(u)))
                            {
                                logger.LogInformation($"User {lodestoneUser.User.UID} not in allowed roles, deleting group alias");
                                group.Alias = null;
                                db.Update(group);
                            }

                            await Task.Delay(100);
                            await db.SaveChangesAsync().ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    logger.LogInformation("No roles for command defined, no cleanup performed");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Something failed during checking vanity user uids");
            }

            logger.LogInformation("Vanity UID cleanup complete");
            await Task.Delay(TimeSpan.FromHours(12), vanityUpdateCts.Token).ConfigureAwait(false);
        }
    }

    private async Task UpdateStatusAsync()
    {
        updateStatusCts = new();
        while (!updateStatusCts.IsCancellationRequested)
        {
            var onlineUsers = await clientService.GetOnlineUsers();
            logger.LogInformation("Users online: " + onlineUsers);
            await discordClient.SetActivityAsync(new Game("Mare for " + onlineUsers + " Users")).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        verificationTaskCts?.Cancel();
        updateStatusCts?.Cancel();

        await discordClient.LogoutAsync().ConfigureAwait(false);
        await discordClient.StopAsync().ConfigureAwait(false);
    }

    public static string GenerateRandomString(int length, string? allowableChars = null)
    {
        if (string.IsNullOrEmpty(allowableChars))
            allowableChars = @"ABCDEFGHJKLMNPQRSTUVWXYZ0123456789";

        // Generate random data
        var rnd = RandomNumberGenerator.GetBytes(length);

        // Generate the output string
        var allowable = allowableChars.ToCharArray();
        var l = allowable.Length;
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = allowable[rnd[i] % l];

        return new string(chars);
    }
}