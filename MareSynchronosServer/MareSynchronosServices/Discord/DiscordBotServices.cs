using System.Collections.Concurrent;
using Discord;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using StackExchange.Redis;

namespace MareSynchronosServices.Discord;

public class DiscordBotServices
{
    public readonly string[] LodestoneServers = ["eu", "na", "jp", "fr", "de"];
    public ConcurrentDictionary<ulong, string> DiscordLodestoneMapping = new();
    public ConcurrentDictionary<ulong, string> DiscordRelinkLodestoneMapping = new();
    public ConcurrentDictionary<ulong, bool> DiscordVerifiedUsers { get; } = new();
    public ConcurrentDictionary<ulong, DateTime> LastVanityChange = new();
    public ConcurrentDictionary<string, DateTime> LastVanityGidChange = new(StringComparer.Ordinal);
    public ConcurrentDictionary<ulong, ulong> ValidInteractions { get; } = new();
    public ConcurrentDictionary<RestRole, string> VanityRoles { get; set; } = new();
    public ConcurrentBag<ulong> VerifiedCaptchaUsers { get; } = new();
    private readonly IConfigurationService<ServicesConfiguration> _configuration;
    private readonly CancellationTokenSource verificationTaskCts = new();
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
        verificationTaskCts.Cancel();
        verificationTaskCts.Dispose();
        return Task.CompletedTask;
    }

    public async Task LogToChannel(string msg)
    {
        if (_guild == null) return;
        Logger.LogInformation("LogToChannel: {msg}", msg);
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

    private async Task RetryAsync(Task action, IUser user, string operation, bool logInfoToChannel = true)
    {
        int retryCount = 0;
        int maxRetries = 5;
        var retryDelay = TimeSpan.FromSeconds(5);

        while (retryCount < maxRetries)
        {
            try
            {
                await action.ConfigureAwait(false);
                if (logInfoToChannel)
                    await LogToChannel($"{user.Mention} {operation} SUCCESS").ConfigureAwait(false);
                break;
            }
            catch (RateLimitedException)
            {
                retryCount++;
                await LogToChannel($"{user.Mention} {operation} RATELIMIT, retry {retryCount} in {retryDelay}.").ConfigureAwait(false);
                await Task.Delay(retryDelay).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await LogToChannel($"{user.Mention} {operation} FAILED: {ex.Message}").ConfigureAwait(false);
                break;
            }
        }

        if (retryCount == maxRetries)
        {
            await LogToChannel($"{user.Mention} FAILED: RetryCount exceeded.").ConfigureAwait(false);
        }
    }

    public async Task RemoveRegisteredRoleAsync(IUser user)
    {
        var registeredRole = _configuration.GetValueOrDefault<ulong?>(nameof(ServicesConfiguration.DiscordRoleRegistered), null);
        if (registeredRole == null) return;
        var restUser = await _guild.GetUserAsync(user.Id).ConfigureAwait(false);
        if (restUser == null) return;
        if (!restUser.RoleIds.Contains(registeredRole.Value)) return;
        await RetryAsync(restUser.RemoveRoleAsync(registeredRole.Value), user, $"Remove Registered Role").ConfigureAwait(false);
    }

    public async Task AddRegisteredRoleAsync(IUser user)
    {
        var registeredRole = _configuration.GetValueOrDefault<ulong?>(nameof(ServicesConfiguration.DiscordRoleRegistered), null);
        if (registeredRole == null) return;
        var restUser = await _guild.GetUserAsync(user.Id).ConfigureAwait(false);
        if (restUser == null) return;
        if (restUser.RoleIds.Contains(registeredRole.Value)) return;
        await RetryAsync(restUser.AddRoleAsync(registeredRole.Value), user, $"Add Registered Role").ConfigureAwait(false);
    }

    public async Task<bool> AddRegisteredRoleAsync(RestGuildUser user, RestRole role)
    {
        if (user.RoleIds.Contains(role.Id)) return false;
        await RetryAsync(user.AddRoleAsync(role), user, $"Add Registered Role", false).ConfigureAwait(false);
        return true;
    }

    public async Task KickUserAsync(RestGuildUser user)
    {
        await RetryAsync(user.KickAsync("No registration found"), user, "Kick").ConfigureAwait(false);
    }

    private async Task ProcessVerificationQueue()
    {
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