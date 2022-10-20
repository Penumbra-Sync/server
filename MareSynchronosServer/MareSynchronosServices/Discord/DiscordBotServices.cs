using Discord;
using MareSynchronosShared.Data;
using System;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Discord.WebSocket;
using System.Linq;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text;
using System.Security.Cryptography;
using System.Threading;

namespace MareSynchronosServices.Discord;

public class DiscordBotServices
{
    public readonly ConcurrentQueue<SocketSlashCommand> verificationQueue = new();
    public ConcurrentDictionary<ulong, DateTime> LastVanityChange = new();
    public ConcurrentDictionary<string, DateTime> LastVanityGidChange = new();
    public ConcurrentDictionary<ulong, string> DiscordLodestoneMapping = new();
    private readonly string[] LodestoneServers = new[] { "eu", "na", "jp", "fr", "de" };
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DiscordBotServices> _logger;
    private readonly MareMetrics _metrics;
    private readonly Random random;
    private CancellationTokenSource? verificationTaskCts;

    public DiscordBotServices(IServiceProvider services, IConfiguration configuration, ILogger<DiscordBotServices> logger, MareMetrics metrics)
    {
        _services = services;
        _configuration = configuration;
        _logger = logger;
        _metrics = metrics;
        random = new();
    }

    public async Task Start()
    {
        _ = ProcessVerificationQueue();
    }

    public async Task Stop()
    {
        verificationTaskCts?.Cancel();
    }

    private async Task ProcessVerificationQueue()
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

                    _logger.LogInformation("Sent login information to user");
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error during queue work");
                }

            }
            await Task.Delay(TimeSpan.FromSeconds(2), verificationTaskCts.Token).ConfigureAwait(false);
        }
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

    private async Task<Embed> HandleVerifyAsync(ulong id)
    {
        var embedBuilder = new EmbedBuilder();

        using var scope = _services.CreateScope();
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

                    _logger.LogInformation("User registered: {userUID}", user.UID);

                    _metrics.IncGauge(MetricsAPI.GaugeUsersRegistered, 1);

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
}
