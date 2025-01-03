using Discord;
using Discord.Interactions;
using Discord.Rest;
using Discord.WebSocket;
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using StackExchange.Redis;

namespace MareSynchronosServices.Discord;

internal class DiscordBot : IHostedService
{
    private readonly DiscordBotServices _botServices;
    private readonly IConfigurationService<ServicesConfiguration> _configurationService;
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly DiscordSocketClient _discordClient;
    private readonly ILogger<DiscordBot> _logger;
    private readonly IDbContextFactory<MareDbContext> _dbContextFactory;
    private readonly IServiceProvider _services;
    private InteractionService _interactionModule;
    private readonly CancellationTokenSource? _processReportQueueCts;
    private CancellationTokenSource? _clientConnectedCts;

    public DiscordBot(DiscordBotServices botServices, IServiceProvider services, IConfigurationService<ServicesConfiguration> configuration,
        IDbContextFactory<MareDbContext> dbContextFactory,
        ILogger<DiscordBot> logger, IConnectionMultiplexer connectionMultiplexer)
    {
        _botServices = botServices;
        _services = services;
        _configurationService = configuration;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _connectionMultiplexer = connectionMultiplexer;
        _discordClient = new(new DiscordSocketConfig()
        {
            DefaultRetryMode = RetryMode.AlwaysRetry,
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildMembers
        });

        _discordClient.Log += Log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var token = _configurationService.GetValueOrDefault(nameof(ServicesConfiguration.DiscordBotToken), string.Empty);
        if (!string.IsNullOrEmpty(token))
        {
            _logger.LogInformation("Starting DiscordBot");
            _logger.LogInformation("Using Configuration: " + _configurationService.ToString());

            _interactionModule?.Dispose();
            _interactionModule = new InteractionService(_discordClient);
            _interactionModule.Log += Log;
            await _interactionModule.AddModuleAsync(typeof(MareModule), _services).ConfigureAwait(false);
            await _interactionModule.AddModuleAsync(typeof(MareWizardModule), _services).ConfigureAwait(false);

            await _discordClient.LoginAsync(TokenType.Bot, token).ConfigureAwait(false);
            await _discordClient.StartAsync().ConfigureAwait(false);

            _discordClient.Ready += DiscordClient_Ready;
            _discordClient.InteractionCreated += async (x) =>
            {
                var ctx = new SocketInteractionContext(_discordClient, x);
                await _interactionModule.ExecuteCommandAsync(ctx, _services).ConfigureAwait(false);
            };
            _discordClient.UserJoined += OnUserJoined;

            await _botServices.Start().ConfigureAwait(false);
        }
    }

    private async Task OnUserJoined(SocketGuildUser arg)
    {
        try
        {
            using MareDbContext dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            var alreadyRegistered = await dbContext.LodeStoneAuth.AnyAsync(u => u.DiscordId == arg.Id).ConfigureAwait(false);
            if (alreadyRegistered)
            {
                await _botServices.AddRegisteredRoleAsync(arg).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set user role on join");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_configurationService.GetValueOrDefault(nameof(ServicesConfiguration.DiscordBotToken), string.Empty)))
        {
            await _botServices.Stop().ConfigureAwait(false);
            _processReportQueueCts?.Cancel();
            _clientConnectedCts?.Cancel();

            await _discordClient.LogoutAsync().ConfigureAwait(false);
            await _discordClient.StopAsync().ConfigureAwait(false);
            _interactionModule?.Dispose();
        }
    }

    private async Task DiscordClient_Ready()
    {
        var guild = (await _discordClient.Rest.GetGuildsAsync().ConfigureAwait(false)).First();
        await _interactionModule.RegisterCommandsToGuildAsync(guild.Id, true).ConfigureAwait(false);
        _clientConnectedCts?.Cancel();
        _clientConnectedCts?.Dispose();
        _clientConnectedCts = new();
        _ = UpdateStatusAsync(_clientConnectedCts.Token);

        await CreateOrUpdateModal(guild).ConfigureAwait(false);
        _botServices.UpdateGuild(guild);
        await _botServices.LogToChannel("Bot startup complete.").ConfigureAwait(false);
        _ = UpdateVanityRoles(guild, _clientConnectedCts.Token);
        _ = RemoveUsersNotInVanityRole(_clientConnectedCts.Token);
        _ = RemoveUnregisteredUsers(_clientConnectedCts.Token);
    }

    private async Task UpdateVanityRoles(RestGuild guild, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Updating Vanity Roles");
                Dictionary<ulong, string> vanityRoles = _configurationService.GetValueOrDefault(nameof(ServicesConfiguration.VanityRoles), new Dictionary<ulong, string>());
                if (vanityRoles.Keys.Count != _botServices.VanityRoles.Count)
                {
                    _botServices.VanityRoles.Clear();
                    foreach (var role in vanityRoles)
                    {
                        _logger.LogInformation("Adding Role: {id} => {desc}", role.Key, role.Value);

                        var restrole = guild.GetRole(role.Key);
                        if (restrole != null)
                            _botServices.VanityRoles[restrole] = role.Value;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during UpdateVanityRoles");
            }
        }
    }

    private async Task CreateOrUpdateModal(RestGuild guild)
    {
        _logger.LogInformation("Creating Wizard: Getting Channel");

        var discordChannelForCommands = _configurationService.GetValue<ulong?>(nameof(ServicesConfiguration.DiscordChannelForCommands));
        if (discordChannelForCommands == null)
        {
            _logger.LogWarning("Creating Wizard: No channel configured");
            return;
        }

        IUserMessage? message = null;
        var socketchannel = await _discordClient.GetChannelAsync(discordChannelForCommands.Value).ConfigureAwait(false) as SocketTextChannel;
        var pinnedMessages = await socketchannel.GetPinnedMessagesAsync().ConfigureAwait(false);
        foreach (var msg in pinnedMessages)
        {
            _logger.LogInformation("Creating Wizard: Checking message id {id}, author is: {author}, hasEmbeds: {embeds}", msg.Id, msg.Author.Id, msg.Embeds.Any());
            if (msg.Author.Id == _discordClient.CurrentUser.Id
                && msg.Embeds.Any())
            {
                message = await socketchannel.GetMessageAsync(msg.Id).ConfigureAwait(false) as IUserMessage;
                break;
            }
        }

        _logger.LogInformation("Creating Wizard: Found message id: {id}", message?.Id ?? 0);

        await GenerateOrUpdateWizardMessage(socketchannel, message).ConfigureAwait(false);
    }

    private async Task GenerateOrUpdateWizardMessage(SocketTextChannel channel, IUserMessage? prevMessage)
    {
        EmbedBuilder eb = new EmbedBuilder();
        eb.WithTitle("Mare Services Bot Interaction Service");
        eb.WithDescription("Press \"Start\" to interact with this bot!" + Environment.NewLine + Environment.NewLine
            + "You can handle all of your Mare account needs in this server through the easy to use interactive bot prompt. Just follow the instructions!");
        eb.WithThumbnailUrl("https://raw.githubusercontent.com/Penumbra-Sync/repo/main/MareSynchronos/images/icon.png");
        var cb = new ComponentBuilder();
        cb.WithButton("Start", style: ButtonStyle.Primary, customId: "wizard-captcha:true", emote: Emoji.Parse("➡️"));
        if (prevMessage == null)
        {
            var msg = await channel.SendMessageAsync(embed: eb.Build(), components: cb.Build()).ConfigureAwait(false);
            try
            {
                await msg.PinAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // swallow
            }
        }
        else
        {
            await prevMessage.ModifyAsync(p =>
            {
                p.Embed = eb.Build();
                p.Components = cb.Build();
            }).ConfigureAwait(false);
        }
    }

    private Task Log(LogMessage msg)
    {
        switch (msg.Severity)
        {
            case LogSeverity.Critical:
            case LogSeverity.Error:
                _logger.LogError(msg.Exception, msg.Message); break;
            case LogSeverity.Warning:
                _logger.LogWarning(msg.Exception, msg.Message); break;
            default:
                _logger.LogInformation(msg.Message); break;
        }

        return Task.CompletedTask;
    }

    private async Task RemoveUnregisteredUsers(CancellationToken token)
    {
        var guild = (await _discordClient.Rest.GetGuildsAsync().ConfigureAwait(false)).First();
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ProcessUserRoles(guild, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // do nothing
            }
            catch (Exception ex)
            {
                await _botServices.LogToChannel($"Error during user procesing: {ex.Message}").ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromDays(1)).ConfigureAwait(false);
        }
    }

    private async Task ProcessUserRoles(RestGuild guild, CancellationToken token)
    {
        using MareDbContext dbContext = await _dbContextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var roleId = _configurationService.GetValueOrDefault<ulong?>(nameof(ServicesConfiguration.DiscordRoleRegistered), 0);
        var kickUnregistered = _configurationService.GetValueOrDefault(nameof(ServicesConfiguration.KickNonRegisteredUsers), false);
        if (roleId == null) return;

        var registrationRole = guild.Roles.FirstOrDefault(f => f.Id == roleId.Value);
        var registeredUsers = new HashSet<ulong>(await dbContext.LodeStoneAuth.AsNoTracking().Select(c => c.DiscordId).ToListAsync().ConfigureAwait(false));

        var executionStartTime = DateTimeOffset.UtcNow;

        int processedUsers = 0;
        int addedRoles = 0;
        int kickedUsers = 0;

        await _botServices.LogToChannel($"Starting to process registered users: Adding Role {registrationRole.Name}. Kick Stale Unregistered: {kickUnregistered}.").ConfigureAwait(false);

        await foreach (var userList in guild.GetUsersAsync(new RequestOptions { CancelToken = token }).ConfigureAwait(false))
        {
            _logger.LogInformation("Processing chunk of {count} users, total processed: {proc}, roles added: {added}, users kicked: {kicked}",
                userList.Count, processedUsers, addedRoles, kickedUsers);
            foreach (var user in userList)
            {
                if (registeredUsers.Contains(user.Id))
                {
                    bool roleAdded = await _botServices.AddRegisteredRoleAsync(user, registrationRole).ConfigureAwait(false);
                    if (roleAdded) addedRoles++;
                }
                else
                {
                    if (kickUnregistered)
                    {
                        if ((executionStartTime - user.JoinedAt.Value).TotalDays > 7)
                        {
                            await _botServices.KickUserAsync(user).ConfigureAwait(false);
                            kickedUsers++;
                        }
                    }

                }

                token.ThrowIfCancellationRequested();
                processedUsers++;
            }

            await _botServices.LogToChannel($"Processing registered users finished. Processed {processedUsers} users, added {addedRoles} roles and kicked {kickedUsers}").ConfigureAwait(false);
        }
    }

    private async Task RemoveUsersNotInVanityRole(CancellationToken token)
    {
        var guild = (await _discordClient.Rest.GetGuildsAsync().ConfigureAwait(false)).First();

        while (!token.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation($"Cleaning up Vanity UIDs");
                await _botServices.LogToChannel("Cleaning up Vanity UIDs").ConfigureAwait(false);
                _logger.LogInformation("Getting rest guild {guildName}", guild.Name);
                var restGuild = await _discordClient.Rest.GetGuildAsync(guild.Id).ConfigureAwait(false);

                Dictionary<ulong, string> allowedRoleIds = _configurationService.GetValueOrDefault(nameof(ServicesConfiguration.VanityRoles), new Dictionary<ulong, string>());
                _logger.LogInformation($"Allowed role ids: {string.Join(", ", allowedRoleIds)}");

                if (allowedRoleIds.Any())
                {
                    using var db = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

                    var aliasedUsers = await db.LodeStoneAuth.Include("User")
                        .Where(c => c.User != null && !string.IsNullOrEmpty(c.User.Alias)).ToListAsync().ConfigureAwait(false);
                    var aliasedGroups = await db.Groups.Include(u => u.Owner)
                        .Where(c => !string.IsNullOrEmpty(c.Alias)).ToListAsync().ConfigureAwait(false);

                    foreach (var lodestoneAuth in aliasedUsers)
                    {
                        await CheckVanityForUser(restGuild, allowedRoleIds, db, lodestoneAuth, token).ConfigureAwait(false);

                        await Task.Delay(1000, token).ConfigureAwait(false);
                    }

                    foreach (var group in aliasedGroups)
                    {
                        await CheckVanityForGroup(restGuild, allowedRoleIds, db, group, token).ConfigureAwait(false);

                        await Task.Delay(1000, token).ConfigureAwait(false);
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
            await Task.Delay(TimeSpan.FromHours(12), token).ConfigureAwait(false);
        }
    }

    private async Task CheckVanityForGroup(RestGuild restGuild, Dictionary<ulong, string> allowedRoleIds, MareDbContext db, Group group, CancellationToken token)
    {
        var groupPrimaryUser = group.OwnerUID;
        var groupOwner = await db.Auth.Include(u => u.User).SingleOrDefaultAsync(u => u.UserUID == group.OwnerUID).ConfigureAwait(false);
        if (groupOwner != null && !string.IsNullOrEmpty(groupOwner.PrimaryUserUID))
        {
            groupPrimaryUser = groupOwner.PrimaryUserUID;
        }

        var lodestoneUser = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(f => f.User.UID == groupPrimaryUser).ConfigureAwait(false);
        RestGuildUser discordUser = null;
        if (lodestoneUser != null)
        {
            discordUser = await restGuild.GetUserAsync(lodestoneUser.DiscordId).ConfigureAwait(false);
        }

        _logger.LogInformation($"Checking Group: {group.GID} [{group.Alias}], owned by {group.OwnerUID} ({groupPrimaryUser}), User in Roles: {string.Join(", ", discordUser?.RoleIds ?? new List<ulong>())}");

        if (lodestoneUser == null || discordUser == null || !discordUser.RoleIds.Any(allowedRoleIds.Keys.Contains))
        {
            await _botServices.LogToChannel($"VANITY GID REMOVAL: <@{lodestoneUser?.DiscordId ?? 0}> ({lodestoneUser?.User?.UID}) - GID: {group.GID}, Vanity: {group.Alias}").ConfigureAwait(false);

            _logger.LogInformation($"User {lodestoneUser?.User?.UID ?? "unknown"} not in allowed roles, deleting group alias for {group.GID}");
            group.Alias = null;
            db.Update(group);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }
    }

    private async Task CheckVanityForUser(RestGuild restGuild, Dictionary<ulong, string> allowedRoleIds, MareDbContext db, LodeStoneAuth lodestoneAuth, CancellationToken token)
    {
        var discordUser = await restGuild.GetUserAsync(lodestoneAuth.DiscordId).ConfigureAwait(false);
        _logger.LogInformation($"Checking User: {lodestoneAuth.DiscordId}, {lodestoneAuth.User.UID} ({lodestoneAuth.User.Alias}), User in Roles: {string.Join(", ", discordUser?.RoleIds ?? new List<ulong>())}");

        if (discordUser == null || !discordUser.RoleIds.Any(u => allowedRoleIds.Keys.Contains(u)))
        {
            _logger.LogInformation($"User {lodestoneAuth.User.UID} not in allowed roles, deleting alias");
            await _botServices.LogToChannel($"VANITY UID REMOVAL: <@{lodestoneAuth.DiscordId}> - UID: {lodestoneAuth.User.UID}, Vanity: {lodestoneAuth.User.Alias}").ConfigureAwait(false);
            lodestoneAuth.User.Alias = null;
            var secondaryUsers = await db.Auth.Include(u => u.User).Where(u => u.PrimaryUserUID == lodestoneAuth.User.UID).ToListAsync().ConfigureAwait(false);
            foreach (var secondaryUser in secondaryUsers)
            {
                _logger.LogInformation($"Secondary User {secondaryUser.User.UID} not in allowed roles, deleting alias");

                secondaryUser.User.Alias = null;
                db.Update(secondaryUser.User);
            }
            db.Update(lodestoneAuth.User);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }
    }

    private async Task UpdateStatusAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var endPoint = _connectionMultiplexer.GetEndPoints().First();
            var onlineUsers = await _connectionMultiplexer.GetServer(endPoint).KeysAsync(pattern: "UID:*").CountAsync().ConfigureAwait(false);

            _logger.LogInformation("Users online: " + onlineUsers);
            await _discordClient.SetActivityAsync(new Game("Mare for " + onlineUsers + " Users")).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}