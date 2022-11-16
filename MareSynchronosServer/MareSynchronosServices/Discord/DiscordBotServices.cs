using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using MareSynchronosShared.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Threading;

namespace MareSynchronosServices.Discord;

public class DiscordBotServices
{
    public readonly ConcurrentQueue<KeyValuePair<ulong, Action>> verificationQueue = new();
    public ConcurrentDictionary<ulong, DateTime> LastVanityChange = new();
    public ConcurrentDictionary<string, DateTime> LastVanityGidChange = new();
    public ConcurrentDictionary<ulong, string> DiscordLodestoneMapping = new();
    public ConcurrentDictionary<ulong, string> DiscordRelinkLodestoneMapping = new();
    public readonly string[] LodestoneServers = new[] { "eu", "na", "jp", "fr", "de" };
    public IConfiguration Configuration { get; init; }
    public ILogger<DiscordBotServices> Logger { get; init; }
    public MareMetrics Metrics { get; init; }
    public Random Random { get; init; }
    private CancellationTokenSource? verificationTaskCts;

    public DiscordBotServices(IConfiguration configuration, ILogger<DiscordBotServices> logger, MareMetrics metrics)
    {
        Configuration = configuration.GetRequiredSection("MareSynchronos");
        Logger = logger;
        Metrics = metrics;
        Random = new();
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
                    queueitem.Value.Invoke();

                    Logger.LogInformation("Sent login information to user");
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
