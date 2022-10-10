using System.Security.Claims;
using System.Text.Encodings.Web;
using MareSynchronosServer;
using MareSynchronosShared.Protos;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ISystemClock = Microsoft.AspNetCore.Authentication.ISystemClock;

namespace MareSynchronosShared.Authentication;

public class SecretKeyGrpcAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string AuthScheme = "SecretKeyGrpcAuth";

    private readonly AuthService.AuthServiceClient _authClient;
    private readonly IHttpContextAccessor _accessor;

    public SecretKeyGrpcAuthenticationHandler(IHttpContextAccessor accessor, AuthService.AuthServiceClient authClient,
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock) : base(options, logger, encoder, clock)
    {
        this._authClient = authClient;
        _accessor = accessor;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.User.Claims.Any(c => string.Equals(c.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal)))
        {
            Logger.LogInformation("Claim already exists");
            return AuthenticateResult.Success(new AuthenticationTicket(Context.User, Scheme.Name));
        }

        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            Logger.LogInformation("Request Header was empty");
            authHeader = string.Empty;
        }

        var ip = _accessor.GetIpAddress();

        var authResult = await _authClient.AuthorizeAsync(new AuthRequest() { Ip = ip, SecretKey = authHeader }).ConfigureAwait(false);

        if (!authResult.Success)
        {
            return AuthenticateResult.Fail("Failed Authorization");
        }

        var uid = authResult.Uid;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, uid),
            new(ClaimTypes.Authentication, authHeader)
        };

        var identity = new ClaimsIdentity(claims, nameof(SecretKeyGrpcAuthenticationHandler));
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        Logger.LogInformation("Claim created");

        return AuthenticateResult.Success(ticket);
    }
}
