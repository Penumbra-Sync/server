using MareSynchronos.API;
using MareSynchronosShared;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace MareSynchronosServer.Controllers;

[AllowAnonymous]
[Route(MareAuth.Auth)]
public class JwtController : Controller
{
    private readonly IHttpContextAccessor _accessor;
    private readonly SecretKeyAuthenticatorService _secretKeyAuthenticatorService;
    private readonly IConfigurationService<MareConfigurationAuthBase> _configuration;

    public JwtController(IHttpContextAccessor accessor, SecretKeyAuthenticatorService secretKeyAuthenticatorService, IConfigurationService<MareConfigurationAuthBase> configuration)
    {
        _accessor = accessor;
        _secretKeyAuthenticatorService = secretKeyAuthenticatorService;
        _configuration = configuration;
    }

    [AllowAnonymous]
    [HttpPost(MareAuth.AuthCreate)]
    public async Task<IActionResult> CreateToken(string auth)
    {
        if (string.IsNullOrEmpty(auth)) return BadRequest("No Authkey");

        var ip = _accessor.GetIpAddress();

        var authResult = await _secretKeyAuthenticatorService.AuthorizeAsync(ip, auth);

        if (!authResult.Success) return Unauthorized("Invalid Authkey");

        var token = CreateToken(new List<Claim>()
        {
            new Claim(ClaimTypes.NameIdentifier, authResult.Uid)
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
