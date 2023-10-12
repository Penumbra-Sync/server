using System.Collections.Concurrent;
using Discord.Rest;
using MareSynchronosShared.Metrics;

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
    public List<RestRole> VanityRoles { get; set; } = new();
    private readonly IServiceProvider _serviceProvider;
    private CancellationTokenSource verificationTaskCts;

    public DiscordBotServices(IServiceProvider serviceProvider, ILogger<DiscordBotServices> logger, MareMetrics metrics)
    {
        _serviceProvider = serviceProvider;
        Logger = logger;
        Metrics = metrics;
    }

    public ILogger<DiscordBotServices> Logger { get; init; }
    public MareMetrics Metrics { get; init; }
    public ConcurrentQueue<KeyValuePair<ulong, Action<IServiceProvider>>> VerificationQueue { get; } = new();

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

    private async Task ProcessVerificationQueue()
    {
        verificationTaskCts = new CancellationTokenSource();
        while (!verificationTaskCts.IsCancellationRequested)
        {
            Logger.LogDebug("Processing Verification Queue, Entries: {entr}", VerificationQueue.Count);
            if (VerificationQueue.TryDequeue(out var queueitem))
            {
                try
                {
                    queueitem.Value.Invoke(_serviceProvider);
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Error during queue work");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), verificationTaskCts.Token).ConfigureAwait(false);
        }
    }
}