﻿using MareSynchronos.API;
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
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace MareSynchronosServer.Controllers;

[AllowAnonymous]
[Route(MareAuth.Auth)]
public class JwtController : Controller
{
    private readonly IHttpContextAccessor _accessor;
    private readonly IRedisDatabase _redis;
    private readonly MareDbContext _mareDbContext;
    private readonly SecretKeyAuthenticatorService _secretKeyAuthenticatorService;
    private readonly IConfigurationService<MareConfigurationAuthBase> _configuration;

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
        if (string.IsNullOrEmpty(auth)) return BadRequest("No Authkey");
        if (string.IsNullOrEmpty(charaIdent)) return BadRequest("No CharaIdent");

        var isBanned = await _mareDbContext.BannedUsers.AsNoTracking().AnyAsync(u => u.CharacterIdentification == charaIdent).ConfigureAwait(false);
        if (isBanned) return Unauthorized("Your character is banned from using the service.");

        var ip = _accessor.GetIpAddress();

        var authResult = await _secretKeyAuthenticatorService.AuthorizeAsync(ip, auth);

        if (!authResult.Success && !authResult.TempBan) return Unauthorized("The provided secret key is invalid. Verify your accounts existence and/or recover the secret key.");
        if (!authResult.Success && authResult.TempBan) return Unauthorized("You are temporarily banned. Try connecting again in 5 minutes.");
        if (authResult.Permaban)
        {
            if (!_mareDbContext.BannedUsers.Any(c => c.CharacterIdentification == charaIdent))
            {
                _mareDbContext.BannedUsers.Add(new Banned()
                {
                    CharacterIdentification = charaIdent,
                    Reason = "Autobanned CharacterIdent (" + authResult.Uid + ")"
                });

                await _mareDbContext.SaveChangesAsync();
            }

            var lodestone = await _mareDbContext.LodeStoneAuth.Include(a => a.User).FirstOrDefaultAsync(c => c.User.UID == authResult.Uid);

            if (lodestone != null)
            {
                if (!_mareDbContext.BannedRegistrations.Any(c => c.DiscordIdOrLodestoneAuth == lodestone.HashedLodestoneId))
                {
                    _mareDbContext.BannedRegistrations.Add(new BannedRegistrations()
                    {
                        DiscordIdOrLodestoneAuth = lodestone.HashedLodestoneId
                    });
                }
                if (!_mareDbContext.BannedRegistrations.Any(c => c.DiscordIdOrLodestoneAuth == lodestone.DiscordId.ToString()))
                {
                    _mareDbContext.BannedRegistrations.Add(new BannedRegistrations()
                    {
                        DiscordIdOrLodestoneAuth = lodestone.DiscordId.ToString()
                    });
                }

                await _mareDbContext.SaveChangesAsync();
            }

            return Unauthorized("You are permanently banned.");
        }

        var existingIdent = await _redis.GetAsync<string>("UID:" + authResult.Uid);
        if (!string.IsNullOrEmpty(existingIdent)) return Unauthorized("Already logged in to this account. Reconnect in 60 seconds. If you keep seeing this issue, restart your game.");

        var token = CreateToken(new List<Claim>()
        {
            new Claim(MareClaimTypes.Uid, authResult.Uid),
            new Claim(MareClaimTypes.CharaIdent, charaIdent)
        });

        return Content(token.RawData);
    }

    private JwtSecurityToken CreateToken(IEnumerable<Claim> authClaims)
    {
        var authSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(_configuration.GetValue<string>(nameof(MareConfigurationAuthBase.Jwt))));

        var token = new SecurityTokenDescriptor()
        {
            Subject = new ClaimsIdentity(authClaims),
            SigningCredentials = new SigningCredentials(authSigningKey, SecurityAlgorithms.HmacSha256Signature)
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.CreateJwtSecurityToken(token);
    }
}
