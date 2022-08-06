using Discord;
using Discord.WebSocket;
using MareSynchronosServer.Data;
using MareSynchronosServer.Hubs;
using MareSynchronosServer.Metrics;
using MareSynchronosServer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MareSynchronosServer.Discord
{
    public class DiscordBot : IHostedService
    {
        private readonly IServiceProvider services;
        private readonly IConfiguration configuration;
        private readonly ILogger<DiscordBot> logger;
        private readonly Random random;
        private string authToken = string.Empty;
        DiscordSocketClient discordClient;
        ConcurrentDictionary<ulong, string> DiscordLodestoneMapping = new();
        private Timer _timer;
        private CancellationTokenSource verificationTaskCts;
        private readonly string[] LodestoneServers = new[] { "eu", "na", "jp", "fr", "de" };
        private readonly ConcurrentQueue<SocketSlashCommand> verificationQueue = new();

        private SemaphoreSlim semaphore;

        public DiscordBot(IServiceProvider services, IConfiguration configuration, ILogger<DiscordBot> logger)
        {
            this.services = services;
            this.configuration = configuration;
            this.logger = logger;
            this.verificationQueue = new ConcurrentQueue<SocketSlashCommand>();
            this.semaphore = new SemaphoreSlim(1);

            random = new();
            authToken = configuration.GetValue<string>("DiscordBotToken");

            discordClient = new(new DiscordSocketConfig()
            {
                DefaultRetryMode = RetryMode.AlwaysRetry
            });

            discordClient.Log += Log;
        }

        private async Task DiscordClient_SlashCommandExecuted(SocketSlashCommand arg)
        {
            await semaphore.WaitAsync();
            try
            {
                if (arg.Data.Name == "register")
                {
                    if (arg.Data.Options.FirstOrDefault(f => f.Name == "overwrite_old_account") != null)
                    {
                        await DeletePreviousUserAccount(arg.User.Id);
                    }

                    var modal = new ModalBuilder();
                    modal.WithTitle("Verify with Lodestone");
                    modal.WithCustomId("register_modal");
                    modal.AddTextInput("Enter the Lodestone URL of your Character", "lodestoneurl", TextInputStyle.Short, "https://*.finalfantasyxiv.com/lodestone/character/<CHARACTERID>/", required: true);
                    await arg.RespondWithModalAsync(modal.Build());
                }
                else if (arg.Data.Name == "verify")
                {
                    EmbedBuilder eb = new();
                    if (verificationQueue.Any(u => u.User.Id == arg.User.Id))
                    {
                        eb.WithTitle("Already queued for verfication");
                        eb.WithDescription("You are already queued for verification. Please wait.");
                        await arg.RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true);
                    }
                    else if (!DiscordLodestoneMapping.ContainsKey(arg.User.Id))
                    {
                        eb.WithTitle("Cannot verify registration");
                        eb.WithDescription("You need to **/register** first before you can **/verify**");
                        await arg.RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true);
                    }
                    else
                    {
                        await arg.DeferAsync(ephemeral: true);
                        verificationQueue.Enqueue(arg);
                    }
                }
                else
                {
                    await arg.RespondAsync("idk what you did to get here to start, just follow the instructions as provided.", ephemeral: true);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        private async Task DeletePreviousUserAccount(ulong id)
        {
            using var scope = services.CreateScope();
            using var db = scope.ServiceProvider.GetService<MareDbContext>();
            var discordAuthedUser = await db.LodeStoneAuth.Include(u => u.User).FirstOrDefaultAsync(u => u.DiscordId == id);
            if (discordAuthedUser != null)
            {
                if (discordAuthedUser.User != null)
                {
                    CleanupService.PurgeUser(discordAuthedUser.User, db, configuration);
                }
                else
                {
                    db.Remove(discordAuthedUser);
                }

                await db.SaveChangesAsync();
            }
        }

        private async Task DiscordClient_ModalSubmitted(SocketModal arg)
        {
            if (arg.Data.CustomId == "register_modal")
            {
                var embed = await HandleRegisterModalAsync(arg);
                await arg.RespondAsync(embeds: new Embed[] { embed }, ephemeral: true);
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
                var response = await req.GetAsync($"https://{randomServer}.finalfantasyxiv.com/lodestone/character/{DiscordLodestoneMapping[id]}");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    if (content.Contains(lodestoneAuth.LodestoneAuthString))
                    {
                        DiscordLodestoneMapping.TryRemove(id, out _);

                        using var sha256 = SHA256.Create();
                        var user = new User();

                        var hasValidUid = false;
                        while (!hasValidUid)
                        {
                            var uid = MareHub.GenerateRandomString(10);
                            if (db.Users.Any(u => u.UID == uid)) continue;
                            user.UID = uid;
                            hasValidUid = true;
                        }

                        // make the first registered user on the service to admin
                        if (!await db.Users.AnyAsync())
                        {
                            user.IsAdmin = true;
                        }

                        if (configuration.GetValue<bool>("PurgeUnusedAccounts"))
                        {
                            var purgedDays = configuration.GetValue<int>("PurgeUnusedAccountsPeriodInDays");
                            user.LastLoggedIn = DateTime.UtcNow - TimeSpan.FromDays(purgedDays) + TimeSpan.FromDays(1);
                        }

                        var computedHash = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(MareHub.GenerateRandomString(64)))).Replace("-", "");
                        var auth = new Auth()
                        {
                            HashedKey = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(computedHash)))
                                .Replace("-", ""),
                            User = user,
                        };

                        db.Users.Add(user);
                        db.Auth.Add(auth);

                        logger.LogInformation("User registered: " + user.UID);

                        MareMetrics.UsersRegistered.Inc();

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

                await db.SaveChangesAsync();
            }
            else
            {
                embedBuilder.WithTitle("Your auth has expired or something else went wrong");
                embedBuilder.WithDescription("Start again with **/register**");
                DiscordLodestoneMapping.TryRemove(id, out _);
            }

            return embedBuilder.Build();
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

                var db = scope.ServiceProvider.GetService<MareDbContext>();

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
                    string lodestoneAuth = await GenerateLodestoneAuth(arg.User.Id, hashedLodestoneId, db);
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
            var auth = MareHub.GenerateRandomString(32);
            LodeStoneAuth lsAuth = new LodeStoneAuth()
            {
                DiscordId = discordid,
                HashedLodestoneId = hashedLodestoneId,
                LodestoneAuthString = auth,
                StartedAt = DateTime.UtcNow
            };

            dbContext.Add(lsAuth);
            await dbContext.SaveChangesAsync();

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

            try
            {
                await discordClient.CreateGlobalApplicationCommandAsync(register.Build());
                await discordClient.CreateGlobalApplicationCommandAsync(verify.Build());
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create command");
            }
        }

        private Task Log(LogMessage msg)
        {
            logger.LogInformation(msg.ToString());

            return Task.CompletedTask;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(authToken))
            {
                authToken = configuration.GetValue<string>("DiscordBotToken");

                await discordClient.LoginAsync(TokenType.Bot, authToken);
                await discordClient.StartAsync();

                discordClient.Ready += DiscordClient_Ready;
                discordClient.SlashCommandExecuted += DiscordClient_SlashCommandExecuted;
                discordClient.ModalSubmitted += DiscordClient_ModalSubmitted;

                _timer = new Timer(UpdateStatus, null, TimeSpan.Zero, TimeSpan.FromSeconds(15));

                ProcessQueueWork();
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
                        var dataEmbed = await HandleVerifyAsync(queueitem.User.Id);
                        await queueitem.FollowupAsync(embed: dataEmbed, ephemeral: true);

                        logger.LogInformation("Sent login information to user");
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e.Message);
                    }

                }
                await Task.Delay(TimeSpan.FromSeconds(2), verificationTaskCts.Token);
            }
        }

        private void UpdateStatus(object state)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetService<MareDbContext>();

            var users = db.Users.Count(c => c.CharacterIdentification != null);

            discordClient.SetActivityAsync(new Game("Mare for " + users + " Users"));
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _timer?.Change(Timeout.Infinite, 0);
            verificationTaskCts?.Cancel();

            await discordClient.LogoutAsync();
            await discordClient.StopAsync();
        }
    }
}
