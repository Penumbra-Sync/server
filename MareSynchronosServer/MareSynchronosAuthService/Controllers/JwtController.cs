using MareSynchronos.API.Routes;
using MareSynchronosAuthService.Services;
using MareSynchronosShared;
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis.Extensions.Core.Abstractions;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace MareSynchronosAuthService.Controllers;

[AllowAnonymous]
[Route(MareAuth.Auth)]
public class JwtController : Controller
{
    private readonly ILogger<JwtController> _logger;
    private readonly IHttpContextAccessor _accessor;
    private readonly IConfigurationService<AuthServiceConfiguration> _configuration;
    private readonly MareDbContext _mareDbContext;
    private readonly IRedisDatabase _redis;
    private readonly GeoIPService _geoIPProvider;
    private readonly SecretKeyAuthenticatorService _secretKeyAuthenticatorService;

    public JwtController(ILogger<JwtController> logger,
        IHttpContextAccessor accessor, MareDbContext mareDbContext,
        SecretKeyAuthenticatorService secretKeyAuthenticatorService,
        IConfigurationService<AuthServiceConfiguration> configuration,
        IRedisDatabase redisDb, GeoIPService geoIPProvider)
    {
        _logger = logger;
        _accessor = accessor;
        _redis = redisDb;
        _geoIPProvider = geoIPProvider;
        _mareDbContext = mareDbContext;
        _secretKeyAuthenticatorService = secretKeyAuthenticatorService;
        _configuration = configuration;
    }

    [AllowAnonymous]
    [HttpPost(MareAuth.Auth_CreateIdent)]
    public async Task<IActionResult> CreateToken(string auth, string charaIdent)
    {
        return await AuthenticateInternal(auth, charaIdent).ConfigureAwait(false);
    }

    [Authorize(Policy = "Authenticated")]
    [HttpGet("renewToken")]
    public async Task<IActionResult> RenewToken()
    {
        try
        {
            var uid = HttpContext.User.Claims.Single(p => string.Equals(p.Type, MareClaimTypes.Uid, StringComparison.Ordinal))!.Value;
            var ident = HttpContext.User.Claims.Single(p => string.Equals(p.Type, MareClaimTypes.CharaIdent, StringComparison.Ordinal))!.Value;
            var alias = HttpContext.User.Claims.SingleOrDefault(p => string.Equals(p.Type, MareClaimTypes.Alias))?.Value ?? string.Empty;

            if (await _mareDbContext.Auth.Where(u => u.UserUID == uid || u.PrimaryUserUID == uid).AnyAsync(a => a.MarkForBan))
            {
                var userAuth = await _mareDbContext.Auth.SingleAsync(u => u.UserUID == uid);
                await EnsureBan(uid, userAuth.PrimaryUserUID, ident);

                return Unauthorized("Your Mare account is banned.");
            }

            if (await IsIdentBanned(ident))
            {
                return Unauthorized("Your XIV service account is banned from using the service.");
            }

            _logger.LogInformation("RenewToken:SUCCESS:{id}:{ident}", uid, ident);
            return await CreateJwtFromId(uid, ident, alias);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenewToken:FAILURE");
            return Unauthorized("Unknown error while renewing authentication token");
        }
    }

    private async Task<IActionResult> AuthenticateInternal(string auth, string charaIdent)
    {
        try
        {
            if (string.IsNullOrEmpty(auth)) return BadRequest("No Authkey");
            if (string.IsNullOrEmpty(charaIdent)) return BadRequest("No CharaIdent");

            var ip = _accessor.GetIpAddress();

            var authResult = await _secretKeyAuthenticatorService.AuthorizeAsync(ip, auth);

            if (await IsIdentBanned(charaIdent))
            {
                _logger.LogWarning("Authenticate:IDENTBAN:{id}:{ident}", authResult.Uid, charaIdent);
                return Unauthorized("Your XIV service account is banned from using the service.");
            }

            if (!authResult.Success && !authResult.TempBan)
            {
                _logger.LogWarning("Authenticate:INVALID:{id}:{ident}", authResult?.Uid ?? "NOUID", charaIdent);
                return Unauthorized("The provided secret key is invalid. Verify your Mare accounts existence and/or recover the secret key.");
            }
            if (!authResult.Success && authResult.TempBan)
            {
                _logger.LogWarning("Authenticate:TEMPBAN:{id}:{ident}", authResult.Uid ?? "NOUID", charaIdent);
                return Unauthorized("Due to an excessive amount of failed authentication attempts you are temporarily banned. Check your Secret Key configuration and try connecting again in 5 minutes.");
            }

            if (authResult.Permaban || authResult.MarkedForBan)
            {
                if (authResult.MarkedForBan)
                {
                    _logger.LogWarning("Authenticate:MARKBAN:{id}:{primaryid}:{ident}", authResult.Uid, authResult.PrimaryUid, charaIdent);
                    await EnsureBan(authResult.Uid!, authResult.PrimaryUid, charaIdent);
                }

                _logger.LogWarning("Authenticate:UIDBAN:{id}:{ident}", authResult.Uid, charaIdent);
                return Unauthorized("Your Mare account is banned from using the service.");
            }

            var existingIdent = await _redis.GetAsync<string>("UID:" + authResult.Uid);
            if (!string.IsNullOrEmpty(existingIdent))
            {
                _logger.LogWarning("Authenticate:DUPLICATE:{id}:{ident}", authResult.Uid, charaIdent);
                return Unauthorized("Already logged in to this Mare account. Reconnect in 60 seconds. If you keep seeing this issue, restart your game.");
            }

            _logger.LogInformation("Authenticate:SUCCESS:{id}:{ident}", authResult.Uid, charaIdent);
            return await CreateJwtFromId(authResult.Uid!, charaIdent, authResult.Alias ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Authenticate:UNKNOWN");
            return Unauthorized("Unknown internal server error during authentication");
        }
    }

    private JwtSecurityToken CreateJwt(IEnumerable<Claim> authClaims)
    {
        var authSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(_configuration.GetValue<string>(nameof(MareConfigurationBase.Jwt))));

        var token = new SecurityTokenDescriptor()
        {
            Subject = new ClaimsIdentity(authClaims),
            SigningCredentials = new SigningCredentials(authSigningKey, SecurityAlgorithms.HmacSha256Signature),
            Expires = new(long.Parse(authClaims.First(f => string.Equals(f.Type, MareClaimTypes.Expires, StringComparison.Ordinal)).Value!, CultureInfo.InvariantCulture), DateTimeKind.Utc),
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.CreateJwtSecurityToken(token);
    }

    private async Task<IActionResult> CreateJwtFromId(string uid, string charaIdent, string alias)
    {
        var token = CreateJwt(new List<Claim>()
        {
            new Claim(MareClaimTypes.Uid, uid),
            new Claim(MareClaimTypes.CharaIdent, charaIdent),
            new Claim(MareClaimTypes.Alias, alias),
            new Claim(MareClaimTypes.Expires, DateTime.UtcNow.AddHours(6).Ticks.ToString(CultureInfo.InvariantCulture)),
            new Claim(MareClaimTypes.Continent, await _geoIPProvider.GetCountryFromIP(_accessor))
        });

        return Content(token.RawData);
    }

    private async Task EnsureBan(string uid, string? primaryUid, string charaIdent)
    {
        if (!_mareDbContext.BannedUsers.Any(c => c.CharacterIdentification == charaIdent))
        {
            _mareDbContext.BannedUsers.Add(new Banned()
            {
                CharacterIdentification = charaIdent,
                Reason = "Autobanned CharacterIdent (" + uid + ")",
            });
        }

        var uidToLookFor = primaryUid ?? uid;

        var primaryUserAuth = await _mareDbContext.Auth.FirstAsync(f => f.UserUID == uidToLookFor);
        primaryUserAuth.MarkForBan = false;
        primaryUserAuth.IsBanned = true;

        var lodestone = await _mareDbContext.LodeStoneAuth.Include(a => a.User).FirstOrDefaultAsync(c => c.User.UID == uidToLookFor);

        if (lodestone != null)
        {
            if (!_mareDbContext.BannedRegistrations.Any(c => c.DiscordIdOrLodestoneAuth == lodestone.HashedLodestoneId))
            {
                _mareDbContext.BannedRegistrations.Add(new BannedRegistrations()
                {
                    DiscordIdOrLodestoneAuth = lodestone.HashedLodestoneId,
                });
            }
            if (!_mareDbContext.BannedRegistrations.Any(c => c.DiscordIdOrLodestoneAuth == lodestone.DiscordId.ToString()))
            {
                _mareDbContext.BannedRegistrations.Add(new BannedRegistrations()
                {
                    DiscordIdOrLodestoneAuth = lodestone.DiscordId.ToString(),
                });
            }
        }

        await _mareDbContext.SaveChangesAsync();
    }

    private async Task<bool> IsIdentBanned(string charaIdent)
    {
        return await _mareDbContext.BannedUsers.AsNoTracking().AnyAsync(u => u.CharacterIdentification == charaIdent).ConfigureAwait(false);
    }
}