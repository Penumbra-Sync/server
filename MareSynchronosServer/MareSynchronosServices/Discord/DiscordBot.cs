using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Discord.Rest;
using Discord.WebSocket;
using MareSynchronosServices.Identity;
using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServices.Discord;

internal class DiscordBot : IHostedService
{
    private readonly DiscordBotServices _botServices;
    private readonly IdentityHandler _identityHandler;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DiscordBot> _logger;
    private string _discordAuthToken = string.Empty;
    private readonly DiscordSocketClient _discordClient;
    private CancellationTokenSource? _updateStatusCts;
    private CancellationTokenSource? _vanityUpdateCts;

    public DiscordBot(DiscordBotServices botServices, IdentityHandler identityHandler, IServiceProvider services, IConfiguration configuration, ILogger<DiscordBot> logger)
    {
        _botServices = botServices;
        _identityHandler = identityHandler;
        _services = services;
        _configuration = configuration.GetRequiredSection("MareSynchronos");
        _logger = logger;

        _discordAuthToken = _configuration.GetValue<string>("DiscordBotToken");

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
        var vanityCommandId = commands.First(c => c.Name == "setvanityuid").Id;

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
                                lodestoneAuth.User.Alias = string.Empty;
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
            var onlineUsers = _identityHandler.GetOnlineUsers(string.Empty);
            _logger.LogInformation("Users online: " + onlineUsers);
            await _discordClient.SetActivityAsync(new Game("Mare for " + onlineUsers + " Users")).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_discordAuthToken))
        {
            _discordAuthToken = _configuration.GetValue<string>("DiscordBotToken");

            await _discordClient.LoginAsync(TokenType.Bot, _discordAuthToken).ConfigureAwait(false);
            await _discordClient.StartAsync().ConfigureAwait(false);

            _discordClient.Ready += DiscordClient_Ready;

            await _botServices.Start();
            _ = UpdateStatusAsync();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_discordAuthToken))
        {
            await _botServices.Stop();
            _updateStatusCts?.Cancel();
            _vanityUpdateCts?.Cancel();

            await _discordClient.LogoutAsync().ConfigureAwait(false);
            await _discordClient.StopAsync().ConfigureAwait(false);
        }
    }
}