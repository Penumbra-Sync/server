using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosServer.Services;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using MareSynchronosShared.Protos;
using MareSynchronosShared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;

namespace MareSynchronosServer.Hubs;

public partial class MareHub : Hub
{
    private readonly MareMetrics _mareMetrics;
    private readonly AuthService.AuthServiceClient _authServiceClient;
    private readonly FileService.FileServiceClient _fileServiceClient;
    private readonly SystemInfoService _systemInfoService;
    private readonly IHttpContextAccessor _contextAccessor;
    private readonly IClientIdentificationService _clientIdentService;
    private readonly ILogger<MareHub> _logger;
    private readonly MareDbContext _dbContext;
    private readonly Uri _cdnFullUri;
    public MareHub(MareMetrics mareMetrics, AuthService.AuthServiceClient authServiceClient, FileService.FileServiceClient fileServiceClient,
        MareDbContext mareDbContext, ILogger<MareHub> logger, SystemInfoService systemInfoService, IConfiguration configuration, IHttpContextAccessor contextAccessor,
        IClientIdentificationService clientIdentService)
    {
        _mareMetrics = mareMetrics;
        _authServiceClient = authServiceClient;
        _fileServiceClient = fileServiceClient;
        _systemInfoService = systemInfoService;
        _cdnFullUri = new Uri(configuration.GetRequiredSection("MareSynchronos").GetValue<string>("CdnFullUrl"));
        _contextAccessor = contextAccessor;
        _clientIdentService = clientIdentService;
        _logger = logger;
        _dbContext = mareDbContext;
    }

    [HubMethodName(Api.InvokeHeartbeat)]
    [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
    public async Task<ConnectionDto> Heartbeat(string characterIdentification)
    {
        _mareMetrics.IncCounter(MetricsAPI.CounterInitializedConnections);

        var userId = Context.User!.Claims.SingleOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;

        _logger.LogInformation("Connection from {userId}, CI: {characterIdentification}", userId, characterIdentification);

        await Clients.Caller.SendAsync(Api.OnUpdateSystemInfo, _systemInfoService.SystemInfoDto).ConfigureAwait(false);

        var isBanned = await _dbContext.BannedUsers.AsNoTracking().AnyAsync(u => u.CharacterIdentification == characterIdentification).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(userId) && !isBanned && !string.IsNullOrEmpty(characterIdentification))
        {
            var user = (await _dbContext.Users.SingleAsync(u => u.UID == userId).ConfigureAwait(false));
            var existingIdent = await _clientIdentService.GetCharacterIdentForUid(userId).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(existingIdent) && characterIdentification != existingIdent)
            {
                return new ConnectionDto()
                {
                    ServerVersion = Api.Version
                };
            }

            user.LastLoggedIn = DateTime.UtcNow;
            await _clientIdentService.MarkUserOnline(user.UID, characterIdentification).ConfigureAwait(false);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);

            return new ConnectionDto
            {
                ServerVersion = Api.Version,
                UID = user.UID,
                Alias = user.Alias,
                IsModerator = user.IsModerator,
                IsAdmin = user.IsAdmin
            };
        }

        return new ConnectionDto()
        {
            ServerVersion = Api.Version
        };
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Connection from {ip}", _contextAccessor.GetIpAddress());
        _mareMetrics.IncGauge(MetricsAPI.GaugeConnections);
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override async Task OnDisconnectedAsync(Exception exception)
    {
        _mareMetrics.DecGauge(MetricsAPI.GaugeConnections);

        var userCharaIdent = await _clientIdentService.GetCharacterIdentForUid(AuthenticatedUserId).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(userCharaIdent))
        {
            _mareMetrics.DecGauge(MetricsAPI.GaugeAuthorizedConnections);

            _logger.LogInformation("Disconnect from {id}", AuthenticatedUserId);

            await SendDataToAllPairedUsers(Api.OnUserRemoveOnlinePairedPlayer, userCharaIdent);

            _dbContext.RemoveRange(_dbContext.Files.Where(f => !f.Uploaded && f.UploaderUID == AuthenticatedUserId));

            await _clientIdentService.MarkUserOffline(AuthenticatedUserId).ConfigureAwait(false);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    private async Task<List<PausedEntry>> GetAllPairedClientsWithPauseState(string? uid = null)
    {
        uid ??= AuthenticatedUserId;

        var query = await (from userPair in _dbContext.ClientPairs
                           join otherUserPair in _dbContext.ClientPairs on userPair.OtherUserUID equals otherUserPair.UserUID
                           where otherUserPair.OtherUserUID == uid && userPair.UserUID == uid
                           select new
                           {
                               UID = Convert.ToString(userPair.OtherUserUID),
                               GID = "DIRECT",
                               PauseState = (userPair.IsPaused || otherUserPair.IsPaused)
                           })
                            .Union(
                                (from userGroupPair in _dbContext.GroupPairs
                                 join otherGroupPair in _dbContext.GroupPairs on userGroupPair.GroupGID equals otherGroupPair.GroupGID
                                 where
                                     userGroupPair.GroupUserUID == uid
                                     && otherGroupPair.GroupUserUID != uid
                                 select new
                                 {
                                     UID = Convert.ToString(otherGroupPair.GroupUserUID),
                                     GID = Convert.ToString(otherGroupPair.GroupGID),
                                     PauseState = (userGroupPair.IsPaused || otherGroupPair.IsPaused)
                                 })
                            ).ToListAsync().ConfigureAwait(false);

        var result = query.GroupBy(g => g.UID, g => (g.GID, g.PauseState),
            (key, g) => new PausedEntry { UID = key, PauseStates = g.Select(p => new PauseState() { GID = p.GID == "DIRECT" ? null : p.GID, IsPaused = p.PauseState }).ToList() }).ToList();
        _logger.LogInformation("{id} getallpairedclientswithpausestate", uid);
        foreach (var entry in result)
        {
            _logger.LogInformation("{id}: {uid}, direct: {direct}, group: {group}, overall: {paused}", uid, entry.UID, entry.IsDirectlyPaused, entry.IsPausedPerGroup, entry.IsPaused);
        }
        return result;
    }

    private async Task<List<string>> GetUnpausedUsersExcludingGroup(string excludedGid, string? uid = null)
    {
        uid ??= AuthenticatedUserId;
        var result = await GetAllPairedClientsWithPauseState(uid).ConfigureAwait(false);
        return result.Where(p => p.IsPausedExcludingGroup(excludedGid) == PauseInfo.Unpaused).Select(p => p.UID).ToList();
    }

    private async Task<List<string>> GetAllPairedUnpausedUsers(string? uid = null)
    {
        uid ??= AuthenticatedUserId;
        var ret = await GetAllPairedClientsWithPauseState(uid).ConfigureAwait(false);
        return ret.Where(k => !k.IsPaused).Select(k => k.UID).ToList();
    }

    private async Task<List<UserPair>> GetAllUserPairs(string uid, bool includePaused = true)
    {
        var query =
            from userToOther in _dbContext.ClientPairs
            join otherToUser in _dbContext.ClientPairs
                on new
                {
                    user = userToOther.UserUID,
                    other = userToOther.OtherUserUID

                } equals new
                {
                    user = otherToUser.OtherUserUID,
                    other = otherToUser.UserUID
                }
            where
                userToOther.UserUID == uid
                && (includePaused || (!includePaused && !userToOther.IsPaused && !otherToUser.IsPaused))
            select new UserPair { UserUID = userToOther.UserUID, OtherUserUID = otherToUser.UserUID, UserPausedOther = userToOther.IsPaused, OtherPausedUser = otherToUser.IsPaused };

        return await query.ToListAsync().ConfigureAwait(false);
    }

    private async Task<List<string>> SendDataToAllPairedUsers(string apiMethod, object arg)
    {
        var usersToSendDataTo = await GetAllPairedUnpausedUsers();
        await Clients.Users(usersToSendDataTo).SendAsync(apiMethod, arg).ConfigureAwait(false);

        return usersToSendDataTo;
    }

    protected string AuthenticatedUserId => Context.User?.Claims?.SingleOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value ?? "Unknown";

    protected async Task<User> GetAuthenticatedUserUntrackedAsync()
    {
        return await _dbContext.Users.AsNoTrackingWithIdentityResolution().SingleAsync(u => u.UID == AuthenticatedUserId).ConfigureAwait(false);
    }
}

public record PausedEntry
{
    public string UID { get; set; }
    public List<PauseState> PauseStates { get; set; } = new();

    public PauseInfo IsDirectlyPaused => pauseStateWithoutGroups == null ? PauseInfo.NoConnection
        : PauseStates.First(g => g.GID == null).IsPaused ? PauseInfo.Paused : PauseInfo.Unpaused;

    public PauseInfo IsPausedPerGroup => !pauseStatesWithoutDirect.Any() ? PauseInfo.NoConnection
        : pauseStatesWithoutDirect.All(p => p.IsPaused) ? PauseInfo.Paused : PauseInfo.Unpaused;

    private IEnumerable<PauseState> pauseStatesWithoutDirect => PauseStates.Where(f => f.GID != null);
    private PauseState pauseStateWithoutGroups => PauseStates.SingleOrDefault(p => p.GID == null);

    public bool IsPaused
    {
        get
        {
            var isDirectlyPaused = IsDirectlyPaused;
            bool result;
            if (isDirectlyPaused != PauseInfo.NoConnection)
            {
                result = isDirectlyPaused == PauseInfo.Paused;
            }
            else
            {
                result = IsPausedPerGroup == PauseInfo.Paused;
            }

            return result;
        }
    }

    public PauseInfo IsPausedExcludingGroup(string gid)
    {
        var states = pauseStatesWithoutDirect.Where(f => f.GID != gid).ToList();
        if (!states.Any()) return PauseInfo.NoConnection;
        var result = states.All(p => p.IsPaused);
        if (result) return PauseInfo.Paused;
        return PauseInfo.Unpaused;
    }
}

public enum PauseInfo
{
    NoConnection,
    Paused,
    Unpaused
}

public record PauseState
{
    public string? GID { get; set; }
    public bool IsPaused { get; set; }
}