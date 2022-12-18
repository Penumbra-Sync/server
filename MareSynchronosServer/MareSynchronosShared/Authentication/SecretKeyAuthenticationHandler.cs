using System.Security.Claims;
using System.Text.Encodings.Web;
using MareSynchronosServer;
using MareSynchronosShared.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MareSynchronosShared.Authentication;

public class SecretKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string AuthScheme = "SecretKeyGrpcAuth";

    private readonly MareDbContext _mareDbContext;
    private readonly IHttpContextAccessor _accessor;
    private readonly SecretKeyAuthenticatorService secretKeyAuthenticatorService;

    public SecretKeyAuthenticationHandler(IHttpContextAccessor accessor, SecretKeyAuthenticatorService secretKeyAuthenticatorService,
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock) : base(options, logger, encoder, clock)
    {
        _accessor = accessor;
        this.secretKeyAuthenticatorService = secretKeyAuthenticatorService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            authHeader = string.Empty;
        }

        var ip = _accessor.GetIpAddress();

        var authResult = await secretKeyAuthenticatorService.AuthorizeAsync(ip, authHeader).ConfigureAwait(false);

        if (!authResult.Success)
        {
            return AuthenticateResult.Fail("Failed Authorization");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, authResult.Uid),
            new(ClaimTypes.Authentication, authHeader)
        };

        var identity = new ClaimsIdentity(claims, nameof(SecretKeyAuthenticationHandler));
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
