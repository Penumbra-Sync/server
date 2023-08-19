using MareSynchronos.API.Routes;
using MareSynchronosServer.Authentication;
using MareSynchronosShared;
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis.Extensions.Core.Abstractions;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace MareSynchronosServer.Controllers;

[AllowAnonymous]
[Route(MareAuth.Auth)]
public class JwtController : Controller
{
    private readonly IHttpContextAccessor _accessor;
    private readonly IConfigurationService<MareConfigurationAuthBase> _configuration;
    private readonly MareDbContext _mareDbContext;
    private readonly IRedisDatabase _redis;
    private readonly SecretKeyAuthenticatorService _secretKeyAuthenticatorService;

    public JwtController(IHttpContextAccessor accessor, MareDbContext mareDbContext,
        SecretKeyAuthenticatorService secretKeyAuthenticatorService,
        IConfigurationService<MareConfigurationAuthBase> configuration,
        IRedisDatabase redisDb)
    {
        _accessor = accessor;
        _redis = redisDb;
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
        var uid = HttpContext.User.Claims.Single(p => string.Equals(p.Type, MareClaimTypes.Uid, StringComparison.Ordinal))!.Value;
        var ident = HttpContext.User.Claims.Single(p => string.Equals(p.Type, MareClaimTypes.CharaIdent, StringComparison.Ordinal))!.Value;

        if (await _mareDbContext.Auth.Where(u => u.UserUID == uid || u.PrimaryUserUID == uid).AnyAsync(a => a.IsBanned))
        {
            await EnsureBan(uid, ident);

            return Unauthorized("You are permanently banned.");
        }

        if (await IsIdentBanned(uid, ident))
        {
            return Unauthorized("Your character is banned from using the service.");
        }

        return CreateJwtFromId(uid, ident);
    }

    private async Task<IActionResult> AuthenticateInternal(string auth, string charaIdent)
    {
        if (string.IsNullOrEmpty(auth)) return BadRequest("No Authkey");
        if (string.IsNullOrEmpty(charaIdent)) return BadRequest("No CharaIdent");

        var ip = _accessor.GetIpAddress();

        var authResult = await _secretKeyAuthenticatorService.AuthorizeAsync(ip, auth);

        if (await IsIdentBanned(authResult.Uid, charaIdent))
        {
            return Unauthorized("Your character is banned from using the service.");
        }

        if (!authResult.Success && !authResult.TempBan) return Unauthorized("The provided secret key is invalid. Verify your accounts existence and/or recover the secret key.");
        if (!authResult.Success && authResult.TempBan) return Unauthorized("You are temporarily banned. Try connecting again in 5 minutes.");
        if (authResult.Permaban)
        {
            await EnsureBan(authResult.Uid, charaIdent);

            return Unauthorized("You are permanently banned.");
        }

        var existingIdent = await _redis.GetAsync<string>("UID:" + authResult.Uid);
        if (!string.IsNullOrEmpty(existingIdent)) return Unauthorized("Already logged in to this account. Reconnect in 60 seconds. If you keep seeing this issue, restart your game.");

        return CreateJwtFromId(authResult.Uid, charaIdent);
    }

    private JwtSecurityToken CreateJwt(IEnumerable<Claim> authClaims)
    {
        var authSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(_configuration.GetValue<string>(nameof(MareConfigurationAuthBase.Jwt))));

        var token = new SecurityTokenDescriptor()
        {
            Subject = new ClaimsIdentity(authClaims),
            SigningCredentials = new SigningCredentials(authSigningKey, SecurityAlgorithms.HmacSha256Signature),
            Expires = new(long.Parse(authClaims.First(f => string.Equals(f.Type, MareClaimTypes.Expires, StringComparison.Ordinal)).Value!, CultureInfo.InvariantCulture), DateTimeKind.Utc),
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.CreateJwtSecurityToken(token);
    }

    private IActionResult CreateJwtFromId(string uid, string charaIdent)
    {
        var token = CreateJwt(new List<Claim>()
        {
            new Claim(MareClaimTypes.Uid, uid),
            new Claim(MareClaimTypes.CharaIdent, charaIdent),
            new Claim(MareClaimTypes.Expires, DateTime.UtcNow.AddHours(6).Ticks.ToString(CultureInfo.InvariantCulture))
        });

        return Content(token.RawData);
    }

    private async Task EnsureBan(string uid, string charaIdent)
    {
        if (!_mareDbContext.BannedUsers.Any(c => c.CharacterIdentification == charaIdent))
        {
            _mareDbContext.BannedUsers.Add(new Banned()
            {
                CharacterIdentification = charaIdent,
                Reason = "Autobanned CharacterIdent (" + uid + ")",
            });

            await _mareDbContext.SaveChangesAsync();
        }

        var primaryUser = await _mareDbContext.Auth.Include(a => a.User).FirstOrDefaultAsync(f => f.PrimaryUserUID == uid);

        var toBanUid = primaryUser == null ? uid : primaryUser.UserUID;

        var lodestone = await _mareDbContext.LodeStoneAuth.Include(a => a.User).FirstOrDefaultAsync(c => c.User.UID == toBanUid);

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

            await _mareDbContext.SaveChangesAsync();
        }
    }

    private async Task<bool> IsIdentBanned(string uid, string charaIdent)
    {
        var isBanned = await _mareDbContext.BannedUsers.AsNoTracking().AnyAsync(u => u.CharacterIdentification == charaIdent).ConfigureAwait(false);
        if (isBanned)
        {
            var authToBan = _mareDbContext.Auth.SingleOrDefault(a => a.UserUID == uid);
            if (authToBan != null)
            {
                authToBan.IsBanned = true;
                await _mareDbContext.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        return isBanned;
    }
}