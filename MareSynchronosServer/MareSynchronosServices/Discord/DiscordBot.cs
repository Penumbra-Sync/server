using Discord;
using Discord.Interactions;
using Discord.Rest;
using Discord.WebSocket;
using MareSynchronos.API.Data.Enum;
using MareSynchronos.API.Dto.User;
using MareSynchronos.API.SignalR;
using MareSynchronosServer.Hubs;
using MareSynchronosShared.Data;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Text;
using System.Threading.Channels;

namespace MareSynchronosServices.Discord;

internal class DiscordBot : IHostedService
{
    private readonly DiscordBotServices _botServices;
    private readonly IConfigurationService<ServicesConfiguration> _configurationService;
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly DiscordSocketClient _discordClient;
    private readonly ILogger<DiscordBot> _logger;
    private readonly IHubContext<MareHub> _mareHubContext;
    private readonly IServiceProvider _services;
    private InteractionService _interactionModule;
    private CancellationTokenSource? _processReportQueueCts;
    private CancellationTokenSource? _updateStatusCts;
    private CancellationTokenSource? _vanityUpdateCts;

    public DiscordBot(DiscordBotServices botServices, IServiceProvider services, IConfigurationService<ServicesConfiguration> configuration,
        IHubContext<MareHub> mareHubContext,
        ILogger<DiscordBot> logger, IConnectionMultiplexer connectionMultiplexer)
    {
        _botServices = botServices;
        _services = services;
        _configurationService = configuration;
        _mareHubContext = mareHubContext;
        _logger = logger;
        _connectionMultiplexer = connectionMultiplexer;
        _discordClient = new(new DiscordSocketConfig()
        {
            DefaultRetryMode = RetryMode.AlwaysRetry
        });

        _discordClient.Log += Log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var token = _configurationService.GetValueOrDefault(nameof(ServicesConfiguration.DiscordBotToken), string.Empty);
        if (!string.IsNullOrEmpty(token))
        {
            _interactionModule = new InteractionService(_discordClient);
            _interactionModule.Log += Log;
            await _interactionModule.AddModuleAsync(typeof(MareModule), _services).ConfigureAwait(false);
            await _interactionModule.AddModuleAsync(typeof(MareWizardModule), _services).ConfigureAwait(false);

            await _discordClient.LoginAsync(TokenType.Bot, token).ConfigureAwait(false);
            await _discordClient.StartAsync().ConfigureAwait(false);

            _discordClient.Ready += DiscordClient_Ready;
            _discordClient.ButtonExecuted += ButtonExecutedHandler;
            _discordClient.InteractionCreated += async (x) =>
            {
                var ctx = new SocketInteractionContext(_discordClient, x);
                await _interactionModule.ExecuteCommandAsync(ctx, _services).ConfigureAwait(false);
            };

            await _botServices.Start().ConfigureAwait(false);
            _ = UpdateStatusAsync();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_configurationService.GetValueOrDefault(nameof(ServicesConfiguration.DiscordBotToken), string.Empty)))
        {
            _discordClient.ButtonExecuted -= ButtonExecutedHandler;

            await _botServices.Stop().ConfigureAwait(false);
            _processReportQueueCts?.Cancel();
            _updateStatusCts?.Cancel();
            _vanityUpdateCts?.Cancel();

            await _discordClient.LogoutAsync().ConfigureAwait(false);
            await _discordClient.StopAsync().ConfigureAwait(false);
        }
    }

    private async Task ButtonExecutedHandler(SocketMessageComponent arg)
    {
        var id = arg.Data.CustomId;
        if (!id.StartsWith("mare-report-button", StringComparison.Ordinal)) return;

        var userId = arg.User.Id;
        using var scope = _services.CreateScope();
        using var dbContext = scope.ServiceProvider.GetRequiredService<MareDbContext>();
        var user = await dbContext.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.DiscordId == userId).ConfigureAwait(false);

        if (user == null || (!user.User.IsModerator && !user.User.IsAdmin))
        {
            EmbedBuilder eb = new();
            eb.WithTitle($"Cannot resolve report");
            eb.WithDescription($"<@{userId}>: You have no rights to resolve this report");
            await arg.RespondAsync(embed: eb.Build()).ConfigureAwait(false);
            return;
        }

        id = id.Remove(0, "mare-report-button-".Length);
        var split = id.Split('-', StringSplitOptions.RemoveEmptyEntries);

        var profile = await dbContext.UserProfileData.SingleAsync(u => u.UserUID == split[1]).ConfigureAwait(false);

        var embed = arg.Message.Embeds.First();

        var builder = embed.ToEmbedBuilder();
        var otherPairs = await dbContext.ClientPairs.Where(p => p.UserUID == split[1]).Select(p => p.OtherUserUID).ToListAsync().ConfigureAwait(false);
        switch (split[0])
        {
            case "dismiss":
                builder.AddField("Resolution", $"Dismissed by <@{userId}>");
                builder.WithColor(Color.Green);
                profile.FlaggedForReport = false;
                await _mareHubContext.Clients.User(split[1]).SendAsync(nameof(IMareHub.Client_ReceiveServerMessage),
                        MessageSeverity.Warning, "The Mare profile report against you has been evaluated and your profile re-enabled.")
                    .ConfigureAwait(false);
                break;

            case "banreporting":
                builder.AddField("Resolution", $"Dismissed by <@{userId}>, Reporting user banned");
                builder.WithColor(Color.DarkGreen);
                profile.FlaggedForReport = false;
                var reportingUser = await dbContext.Auth.SingleAsync(u => u.UserUID == split[2]).ConfigureAwait(false);
                reportingUser.IsBanned = true;
                var regReporting = await dbContext.LodeStoneAuth.SingleAsync(u => u.User.UID == reportingUser.UserUID).ConfigureAwait(false);
                dbContext.BannedRegistrations.Add(new MareSynchronosShared.Models.BannedRegistrations()
                {
                    DiscordIdOrLodestoneAuth = regReporting.HashedLodestoneId
                });
                dbContext.BannedRegistrations.Add(new MareSynchronosShared.Models.BannedRegistrations()
                {
                    DiscordIdOrLodestoneAuth = regReporting.DiscordId.ToString()
                });
                await _mareHubContext.Clients.User(split[1]).SendAsync(nameof(IMareHub.Client_ReceiveServerMessage),
                        MessageSeverity.Warning, "The Mare profile report against you has been evaluated and your profile re-enabled.")
                    .ConfigureAwait(false);
                break;

            case "banprofile":
                builder.AddField("Resolution", $"Profile has been banned by <@{userId}>");
                builder.WithColor(Color.Red);
                profile.Base64ProfileImage = null;
                profile.UserDescription = null;
                profile.ProfileDisabled = true;
                profile.FlaggedForReport = false;
                await _mareHubContext.Clients.User(split[1]).SendAsync(nameof(IMareHub.Client_ReceiveServerMessage),
                    MessageSeverity.Warning, "The Mare profile report against you has been evaluated and the profile functionality permanently disabled.")
                    .ConfigureAwait(false);
                break;

            case "banuser":
                builder.AddField("Resolution", $"User has been banned by <@{userId}>");
                builder.WithColor(Color.DarkRed);
                var offendingUser = await dbContext.Auth.SingleAsync(u => u.UserUID == split[1]).ConfigureAwait(false);
                offendingUser.IsBanned = true;
                profile.Base64ProfileImage = null;
                profile.UserDescription = null;
                profile.ProfileDisabled = true;
                var reg = await dbContext.LodeStoneAuth.SingleAsync(u => u.User.UID == offendingUser.UserUID).ConfigureAwait(false);
                dbContext.BannedRegistrations.Add(new MareSynchronosShared.Models.BannedRegistrations()
                {
                    DiscordIdOrLodestoneAuth = reg.HashedLodestoneId
                });
                dbContext.BannedRegistrations.Add(new MareSynchronosShared.Models.BannedRegistrations()
                {
                    DiscordIdOrLodestoneAuth = reg.DiscordId.ToString()
                });
                await _mareHubContext.Clients.User(split[1]).SendAsync(nameof(IMareHub.Client_ReceiveServerMessage),
                    MessageSeverity.Warning, "The Mare profile report against you has been evaluated and your account permanently banned.")
                    .ConfigureAwait(false);
                break;
        }

        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        await _mareHubContext.Clients.Users(otherPairs).SendAsync(nameof(IMareHub.Client_UserUpdateProfile), new UserDto(new(split[1]))).ConfigureAwait(false);
        await _mareHubContext.Clients.User(split[1]).SendAsync(nameof(IMareHub.Client_UserUpdateProfile), new UserDto(new(split[1]))).ConfigureAwait(false);

        await arg.Message.ModifyAsync(msg =>
        {
            msg.Content = arg.Message.Content;
            msg.Components = null;
            msg.Embed = new Optional<Embed>(builder.Build());
        }).ConfigureAwait(false);
    }

    private async Task DiscordClient_Ready()
    {
        var guild = (await _discordClient.Rest.GetGuildsAsync().ConfigureAwait(false)).First();
        await _interactionModule.RegisterCommandsToGuildAsync(guild.Id, true).ConfigureAwait(false);

        await CreateOrUpdateModal(guild).ConfigureAwait(false);
        _ = UpdateVanityRoles(guild);
        _ = RemoveUsersNotInVanityRole();
        _ = ProcessReportsQueue();
    }

    private async Task UpdateVanityRoles(RestGuild guild)
    {
        while (!_updateStatusCts.IsCancellationRequested)
        {
            var vanityRoles = _configurationService.GetValueOrDefault(nameof(ServicesConfiguration.VanityRoles), Array.Empty<ulong>());
            if (vanityRoles.Length != _botServices.VanityRoles.Count)
            {
                _botServices.VanityRoles.Clear();
                foreach (var role in vanityRoles)
                {
                    var restrole = guild.GetRole(role);
                    if (restrole != null) _botServices.VanityRoles.Add(restrole);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(30), _updateStatusCts.Token).ConfigureAwait(false);
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
        cb.WithButton("Start", style: ButtonStyle.Primary, customId: "wizard-home:true", emote: Emoji.Parse("➡️"));
        if (prevMessage == null)
        {
            var msg = await channel.SendMessageAsync(embed: eb.Build(), components: cb.Build()).ConfigureAwait(false);
            await msg.PinAsync().ConfigureAwait(false);
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

    private async Task ProcessReportsQueue()
    {
        var guild = (await _discordClient.Rest.GetGuildsAsync()).First();

        _processReportQueueCts?.Cancel();
        _processReportQueueCts?.Dispose();
        _processReportQueueCts = new();
        var token = _processReportQueueCts.Token;
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            if (_discordClient.ConnectionState != ConnectionState.Connected) continue;
            var reportChannelId = _configurationService.GetValue<ulong?>(nameof(ServicesConfiguration.DiscordChannelForReports));
            if (reportChannelId == null) continue;

            try
            {
                using (var scope = _services.CreateScope())
                {
                    _logger.LogInformation("Checking for Profile Reports");
                    var dbContext = scope.ServiceProvider.GetRequiredService<MareDbContext>();
                    if (!dbContext.UserProfileReports.Any())
                    {
                        continue;
                    }

                    var reports = await dbContext.UserProfileReports.ToListAsync().ConfigureAwait(false);
                    var restChannel = await guild.GetTextChannelAsync(reportChannelId.Value).ConfigureAwait(false);

                    foreach (var report in reports)
                    {
                        var reportedUser = await dbContext.Users.SingleAsync(u => u.UID == report.ReportedUserUID).ConfigureAwait(false);
                        var reportedUserLodestone = await dbContext.LodeStoneAuth.SingleOrDefaultAsync(u => u.User.UID == report.ReportedUserUID).ConfigureAwait(false);
                        var reportingUser = await dbContext.Users.SingleAsync(u => u.UID == report.ReportingUserUID).ConfigureAwait(false);
                        var reportingUserLodestone = await dbContext.LodeStoneAuth.SingleOrDefaultAsync(u => u.User.UID == report.ReportingUserUID).ConfigureAwait(false);
                        var reportedUserProfile = await dbContext.UserProfileData.SingleAsync(u => u.UserUID == report.ReportedUserUID).ConfigureAwait(false);
                        EmbedBuilder eb = new();
                        eb.WithTitle("Mare Synchronos Profile Report");

                        StringBuilder reportedUserSb = new();
                        StringBuilder reportingUserSb = new();
                        reportedUserSb.Append(reportedUser.UID);
                        reportingUserSb.Append(reportingUser.UID);
                        if (reportedUserLodestone != null)
                        {
                            reportedUserSb.AppendLine($" (<@{reportedUserLodestone.DiscordId}>)");
                        }
                        if (reportingUserLodestone != null)
                        {
                            reportingUserSb.AppendLine($" (<@{reportingUserLodestone.DiscordId}>)");
                        }
                        eb.AddField("Reported User", reportedUserSb.ToString());
                        eb.AddField("Reporting User", reportingUserSb.ToString());
                        eb.AddField("Report Date (UTC)", report.ReportDate);
                        eb.AddField("Report Reason", string.IsNullOrWhiteSpace(report.ReportReason) ? "-" : report.ReportReason);
                        eb.AddField("Reported User Profile Description", string.IsNullOrWhiteSpace(reportedUserProfile.UserDescription) ? "-" : reportedUserProfile.UserDescription);
                        eb.AddField("Reported User Profile Is NSFW", reportedUserProfile.IsNSFW);

                        var cb = new ComponentBuilder();
                        cb.WithButton("Dismiss Report", customId: $"mare-report-button-dismiss-{reportedUser.UID}", style: ButtonStyle.Primary);
                        cb.WithButton("Ban profile", customId: $"mare-report-button-banprofile-{reportedUser.UID}", style: ButtonStyle.Secondary);
                        cb.WithButton("Ban user", customId: $"mare-report-button-banuser-{reportedUser.UID}", style: ButtonStyle.Danger);
                        cb.WithButton("Dismiss and Ban Reporting user", customId: $"mare-report-button-banreporting-{reportedUser.UID}-{reportingUser.UID}", style: ButtonStyle.Danger);

                        if (!string.IsNullOrEmpty(reportedUserProfile.Base64ProfileImage))
                        {
                            var fileName = reportedUser.UID + "_profile_" + Guid.NewGuid().ToString("N") + ".png";
                            eb.WithImageUrl($"attachment://{fileName}");
                            using MemoryStream ms = new(Convert.FromBase64String(reportedUserProfile.Base64ProfileImage));
                            await restChannel.SendFileAsync(ms, fileName, "User Report", embed: eb.Build(), components: cb.Build(), isSpoiler: true).ConfigureAwait(false);
                        }
                        else
                        {
                            var msg = await restChannel.SendMessageAsync(embed: eb.Build(), components: cb.Build()).ConfigureAwait(false);
                        }

                        dbContext.Remove(report);
                    }

                    await dbContext.SaveChangesAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process reports");
            }
        }
    }

    private async Task RemoveUsersNotInVanityRole()
    {
        _vanityUpdateCts?.Cancel();
        _vanityUpdateCts?.Dispose();
        _vanityUpdateCts = new();
        var token = _vanityUpdateCts.Token;
        var guild = (await _discordClient.Rest.GetGuildsAsync()).First();
        var appId = await _discordClient.GetApplicationInfoAsync().ConfigureAwait(false);

        while (!token.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation($"Cleaning up Vanity UIDs");
                _logger.LogInformation("Getting application commands from guild {guildName}", guild.Name);
                var restGuild = await _discordClient.Rest.GetGuildAsync(guild.Id);

                ulong[] allowedRoleIds = _configurationService.GetValueOrDefault(nameof(ServicesConfiguration.VanityRoles), Array.Empty<ulong>());
                _logger.LogInformation($"Allowed role ids: {string.Join(", ", allowedRoleIds)}");

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
}