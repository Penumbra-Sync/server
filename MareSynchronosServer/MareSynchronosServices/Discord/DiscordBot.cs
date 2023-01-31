using Discord;
using Discord.Interactions;
using Discord.Rest;
using Discord.WebSocket;
using MareSynchronosShared.Data;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace MareSynchronosServices.Discord;

internal class DiscordBot : IHostedService
{
    private readonly DiscordBotServices _botServices;
    private readonly IServiceProvider _services;
    private readonly IConfigurationService<ServicesConfiguration> _configurationService;
    private readonly ILogger<DiscordBot> _logger;
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly DiscordSocketClient _discordClient;
    private CancellationTokenSource? _updateStatusCts;
    private CancellationTokenSource? _vanityUpdateCts;

    public DiscordBot(DiscordBotServices botServices, IServiceProvider services, IConfigurationService<ServicesConfiguration> configuration,
        ILogger<DiscordBot> logger, IConnectionMultiplexer connectionMultiplexer)
    {
        _botServices = botServices;
        _services = services;
        _configurationService = configuration;
        _logger = logger;
        _connectionMultiplexer = connectionMultiplexer;
        _discordClient = new(new DiscordSocketConfig()
        {
            DefaultRetryMode = RetryMode.AlwaysRetry
        });

        _discordClient.Log += Log;
    }

    private async Task DiscordClient_Ready()
    {
        var guild = (await _discordClient.Rest.GetGuildsAsync()).First();
        var interactionModule = new InteractionService(_discordClient);
        await interactionModule.AddModuleAsync(typeof(MareModule), _services).ConfigureAwait(false);
        await interactionModule.RegisterCommandsToGuildAsync(guild.Id, true).ConfigureAwait(false);

        _discordClient.InteractionCreated += async (x) =>
        {
            var ctx = new SocketInteractionContext(_discordClient, x);
            await interactionModule.ExecuteCommandAsync(ctx, _services);
        };

        _ = RemoveUsersNotInVanityRole();
    }

    private Task Log(LogMessage msg)
    {
        _logger.LogInformation("{msg}", msg);

        return Task.CompletedTask;
    }

    private async Task RemoveUsersNotInVanityRole()
    {
        _vanityUpdateCts = new();
        var guild = (await _discordClient.Rest.GetGuildsAsync()).First();
        var commands = await guild.GetApplicationCommandsAsync();
        var appId = await _discordClient.GetApplicationInfoAsync().ConfigureAwait(false);
        var vanityCommandId = commands.First(c => c.ApplicationId == appId.Id && c.Name == "setvanityuid").Id;

        while (!_vanityUpdateCts.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation($"Cleaning up Vanity UIDs");
                _logger.LogInformation("Getting application commands from guild {guildName}", guild.Name);
                var restGuild = await _discordClient.Rest.GetGuildAsync(guild.Id);
                var vanityCommand = await restGuild.GetSlashCommandAsync(vanityCommandId).ConfigureAwait(false);
                GuildApplicationCommandPermission commandPermissions = null;
                try
                {
                    _logger.LogInformation($"Getting command permissions");
                    commandPermissions = await vanityCommand.GetCommandPermission().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting command permissions");
                    throw new Exception("Can't get command permissions");
                }

                _logger.LogInformation($"Getting allowed role ids from permissions");
                List<ulong> allowedRoleIds = new();
                try
                {
                    allowedRoleIds = (from perm in commandPermissions.Permissions where perm.TargetType == ApplicationCommandPermissionTarget.Role where perm.Permission select perm.TargetId).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error resolving permissions to roles");
                }

                _logger.LogInformation($"Found allowed role ids: {string.Join(", ", allowedRoleIds)}");

                if (allowedRoleIds.Any())
                {
                    await using var scope = _services.CreateAsyncScope();
                    await using (var db = scope.ServiceProvider.GetRequiredService<MareDbContext>())
                    {
                        var aliasedUsers = await db.LodeStoneAuth.Include("User")
                            .Where(c => c.User != null && !string.IsNullOrEmpty(c.User.Alias)).ToListAsync().ConfigureAwait(false);
                        var aliasedGroups = await db.Groups.Include(u => u.Owner)
                            .Where(c => !string.IsNullOrEmpty(c.Alias)).ToListAsync().ConfigureAwait(false);

                        foreach (var lodestoneAuth in aliasedUsers)
                        {
                            var discordUser = await restGuild.GetUserAsync(lodestoneAuth.DiscordId).ConfigureAwait(false);
                            _logger.LogInformation($"Checking User: {lodestoneAuth.DiscordId}, {lodestoneAuth.User.UID} ({lodestoneAuth.User.Alias}), User in Roles: {string.Join(", ", discordUser?.RoleIds ?? new List<ulong>())}");

                            if (discordUser == null || !discordUser.RoleIds.Any(u => allowedRoleIds.Contains(u)))
                            {
                                _logger.LogInformation($"User {lodestoneAuth.User.UID} not in allowed roles, deleting alias");
                                lodestoneAuth.User.Alias = null;
                                var secondaryUsers = await db.Auth.Include(u => u.User).Where(u => u.PrimaryUserUID == lodestoneAuth.User.UID).ToListAsync().ConfigureAwait(false);
                                foreach (var secondaryUser in secondaryUsers)
                                {
                                    _logger.LogInformation($"Secondary User {secondaryUser.User.UID} not in allowed roles, deleting alias");

                                    secondaryUser.User.Alias = null;
                                    db.Update(secondaryUser.User);
                                }
                                db.Update(lodestoneAuth.User);
                            }

                            await db.SaveChangesAsync().ConfigureAwait(false);
                            await Task.Delay(1000);
                        }

                        foreach (var group in aliasedGroups)
                        {
                            var lodestoneUser = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(f => f.User.UID == group.OwnerUID).ConfigureAwait(false);
                            RestGuildUser discordUser = null;
                            if (lodestoneUser != null)
                            {
                                discordUser = await restGuild.GetUserAsync(lodestoneUser.DiscordId).ConfigureAwait(false);
                            }

                            _logger.LogInformation($"Checking Group: {group.GID}, owned by {lodestoneUser?.User?.UID ?? string.Empty} ({lodestoneUser?.User?.Alias ?? string.Empty}), User in Roles: {string.Join(", ", discordUser?.RoleIds ?? new List<ulong>())}");

                            if (lodestoneUser == null || discordUser == null || !discordUser.RoleIds.Any(u => allowedRoleIds.Contains(u)))
                            {
                                _logger.LogInformation($"User {lodestoneUser.User.UID} not in allowed roles, deleting group alias");
                                group.Alias = null;
                                db.Update(group);
                            }

                            await db.SaveChangesAsync().ConfigureAwait(false);
                            await Task.Delay(1000);
                        }
                    }
                }
                else
                {
                    _logger.LogInformation("No roles for command defined, no cleanup performed");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Something failed during checking vanity user uids");
            }

            _logger.LogInformation("Vanity UID cleanup complete");
            await Task.Delay(TimeSpan.FromHours(12), _vanityUpdateCts.Token).ConfigureAwait(false);
        }
    }

    private async Task UpdateStatusAsync()
    {
        _updateStatusCts = new();
        while (!_updateStatusCts.IsCancellationRequested)
        {
            var endPoint = _connectionMultiplexer.GetEndPoints().First();
            var onlineUsers = await _connectionMultiplexer.GetServer(endPoint).KeysAsync(pattern: "UID:*").CountAsync();

            _logger.LogInformation("Users online: " + onlineUsers);
            await _discordClient.SetActivityAsync(new Game("Mare for " + onlineUsers + " Users")).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var token = _configurationService.GetValueOrDefault(nameof(ServicesConfiguration.DiscordBotToken), string.Empty);
        if (!string.IsNullOrEmpty(token))
        {
            await _discordClient.LoginAsync(TokenType.Bot, token).ConfigureAwait(false);
            await _discordClient.StartAsync().ConfigureAwait(false);

            _discordClient.Ready += DiscordClient_Ready;

            await _botServices.Start();
            _ = UpdateStatusAsync();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_configurationService.GetValueOrDefault(nameof(ServicesConfiguration.DiscordBotToken), string.Empty)))
        {
            await _botServices.Stop();
            _updateStatusCts?.Cancel();
            _vanityUpdateCts?.Cancel();

            await _discordClient.LogoutAsync().ConfigureAwait(false);
            await _discordClient.StopAsync().ConfigureAwait(false);
        }
    }
}