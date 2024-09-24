using System.Collections.Concurrent;
using Discord.Rest;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;

namespace MareSynchronosServices.Discord;

public class DiscordBotServices
{
    public readonly string[] LodestoneServers = new[] { "eu", "na", "jp", "fr", "de" };
    public ConcurrentDictionary<ulong, string> DiscordLodestoneMapping = new();
    public ConcurrentDictionary<ulong, string> DiscordRelinkLodestoneMapping = new();
    public ConcurrentDictionary<ulong, bool> DiscordVerifiedUsers { get; } = new();
    public ConcurrentDictionary<ulong, DateTime> LastVanityChange = new();
    public ConcurrentDictionary<string, DateTime> LastVanityGidChange = new();
    public ConcurrentDictionary<ulong, ulong> ValidInteractions { get; } = new();
    public Dictionary<RestRole, string> VanityRoles { get; set; } = new();
    public ConcurrentBag<ulong> VerifiedCaptchaUsers { get; } = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfigurationService<ServicesConfiguration> _configuration;
    private CancellationTokenSource verificationTaskCts;
    private RestGuild? _guild;
    private ulong? _logChannelId;
    private RestTextChannel? _logChannel;

    public DiscordBotServices(ILogger<DiscordBotServices> logger, MareMetrics metrics,
        IConfigurationService<ServicesConfiguration> configuration)
    {
        Logger = logger;
        Metrics = metrics;
        _configuration = configuration;
    }

    public ILogger<DiscordBotServices> Logger { get; init; }
    public MareMetrics Metrics { get; init; }
    public ConcurrentQueue<KeyValuePair<ulong, Func<DiscordBotServices, Task>>> VerificationQueue { get; } = new();

    public Task Start()
    {
        _ = ProcessVerificationQueue();
        return Task.CompletedTask;
    }

    public Task Stop()
    {
        verificationTaskCts?.Cancel();
        return Task.CompletedTask;
    }

    public async Task LogToChannel(string msg)
    {
        if (_guild == null) return;
        var logChannelId = _configuration.GetValueOrDefault<ulong?>(nameof(ServicesConfiguration.DiscordChannelForBotLog), null);
        if (logChannelId == null) return;
        if (logChannelId != _logChannelId)
        {
            try
            {
                _logChannelId = logChannelId;
                _logChannel = await _guild.GetTextChannelAsync(logChannelId.Value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Could not get bot log channel");
            }
        }

        if (_logChannel == null) return;

        await _logChannel.SendMessageAsync(msg).ConfigureAwait(false);
    }

    private async Task ProcessVerificationQueue()
    {
        verificationTaskCts = new CancellationTokenSource();
        while (!verificationTaskCts.IsCancellationRequested)
        {
            Logger.LogDebug("Processing Verification Queue, Entries: {entr}", VerificationQueue.Count);
            if (VerificationQueue.TryPeek(out var queueitem))
            {
                try
                {
                    await queueitem.Value.Invoke(this).ConfigureAwait(false);
                    Logger.LogInformation("Processed Verification for {key}", queueitem.Key);
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Error during queue work");
                }
                finally
                {
                    VerificationQueue.TryDequeue(out _);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), verificationTaskCts.Token).ConfigureAwait(false);
        }
    }

    internal void UpdateGuild(RestGuild guild)
    {
        _guild = guild;
    }
}