using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using MareSynchronosShared.Metrics;
using Microsoft.Extensions.Logging;
using System.Threading;
using Microsoft.Extensions.Options;

namespace MareSynchronosServices.Discord;

public class DiscordBotServices
{
    public readonly ConcurrentQueue<KeyValuePair<ulong, Action<IServiceProvider>>> verificationQueue = new();
    public ConcurrentDictionary<ulong, DateTime> LastVanityChange = new();
    public ConcurrentDictionary<string, DateTime> LastVanityGidChange = new();
    public ConcurrentDictionary<ulong, string> DiscordLodestoneMapping = new();
    public ConcurrentDictionary<ulong, string> DiscordRelinkLodestoneMapping = new();
    public readonly string[] LodestoneServers = new[] { "eu", "na", "jp", "fr", "de" };
    private readonly IServiceProvider _serviceProvider;

    public ServicesConfiguration Configuration { get; init; }
    public ILogger<DiscordBotServices> Logger { get; init; }
    public MareMetrics Metrics { get; init; }
    public Random Random { get; init; }
    private CancellationTokenSource? verificationTaskCts;

    public DiscordBotServices(IOptions<ServicesConfiguration> configuration, IServiceProvider serviceProvider, ILogger<DiscordBotServices> logger, MareMetrics metrics)
    {
        Configuration = configuration.Value;
        _serviceProvider = serviceProvider;
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
                    queueitem.Value.Invoke(_serviceProvider);

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
