using MareSynchronosShared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using System;
using System.Linq;
using System.Security.Claims;
using MareSynchronosServer.Services;
using System.Collections.Generic;

namespace MareSynchronosServer.Utils;

public class IdentityAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string AuthScheme = "IdentityAuth";

    private readonly SecretKeyGrpcAuthenticationHandler secretKeyGrpcAuthenticationHandler;
    private readonly GrpcClientIdentificationService identClient;

    public IdentityAuthenticationHandler(SecretKeyGrpcAuthenticationHandler secretKeyGrpcAuthenticationHandler, GrpcClientIdentificationService identClient,
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock) : base(options, logger, encoder, clock)
    {
        this.secretKeyGrpcAuthenticationHandler = secretKeyGrpcAuthenticationHandler;
        this.identClient = identClient;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var secretKeyAuth = await secretKeyGrpcAuthenticationHandler.AuthenticateAsync().ConfigureAwait(false);
        if (!secretKeyAuth.Succeeded)
        {
            return secretKeyAuth;
        }

        var uid = secretKeyAuth.Principal.Claims.Single(g => string.Equals(g.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal)).Value;

        var ident = identClient.GetCharacterIdentForUid(uid);

        if (ident == null)
        {
            return AuthenticateResult.Fail("Failed to authorize");
        }

        var isOnCurrent = identClient.IsOnCurrentServer(uid);
        if (!isOnCurrent)
        {
            identClient.MarkUserOnline(uid, ident);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, uid),
            new(ClaimTypes.Authentication, secretKeyAuth.Principal.Claims.Single(g=> string.Equals(g.Type, ClaimTypes.Authentication, StringComparison.Ordinal)).Value),
            new(ClaimTypes.GivenName, ident)
        };

        var identity = new ClaimsIdentity(claims, nameof(SecretKeyGrpcAuthenticationHandler));
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}