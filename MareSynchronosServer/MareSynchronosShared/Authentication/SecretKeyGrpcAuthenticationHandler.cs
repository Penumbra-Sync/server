using System.Security.Claims;
using System.Text.Encodings.Web;
using MareSynchronosServer;
using MareSynchronosShared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ISystemClock = Microsoft.AspNetCore.Authentication.ISystemClock;

namespace MareSynchronosShared.Authentication;

public class SecretKeyGrpcAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string AuthScheme = "SecretKeyGrpcAuth";

    private readonly GrpcAuthenticationService _grpcAuthService;
    private readonly IHttpContextAccessor _accessor;

    public SecretKeyGrpcAuthenticationHandler(IHttpContextAccessor accessor, GrpcAuthenticationService authClient,
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock) : base(options, logger, encoder, clock)
    {
        this._grpcAuthService = authClient;
        _accessor = accessor;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            authHeader = string.Empty;
        }

        var ip = _accessor.GetIpAddress();

        var authResult = await _grpcAuthService.AuthorizeAsync(ip, authHeader).ConfigureAwait(false);

        if (!authResult.Success)
        {
            return AuthenticateResult.Fail("Failed Authorization");
        }

        var uid = authResult.Uid;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, uid.Uid),
            new(ClaimTypes.Authentication, authHeader)
        };

        var identity = new ClaimsIdentity(claims, nameof(SecretKeyGrpcAuthenticationHandler));
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
