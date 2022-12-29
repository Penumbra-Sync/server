using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MareSynchronosShared.Authentication;

public class SecretKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string AuthScheme = "SecretKeyGrpcAuth";

    private readonly IHttpContextAccessor _accessor;
    private readonly SecretKeyAuthenticatorService secretKeyAuthenticatorService;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> IPLocks = new(StringComparer.Ordinal);

    public SecretKeyAuthenticationHandler(IHttpContextAccessor accessor, SecretKeyAuthenticatorService secretKeyAuthenticatorService,
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock) : base(options, logger, encoder, clock)
    {
        _accessor = accessor;
        this.secretKeyAuthenticatorService = secretKeyAuthenticatorService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var endpoint = Context.GetEndpoint();
        if (endpoint?.Metadata?.GetMetadata<IAllowAnonymous>() != null)
        {
            return AuthenticateResult.NoResult();
        }

        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            return AuthenticateResult.Fail("Failed Authorization");
        }

        var ip = _accessor.GetIpAddress();

        if (!IPLocks.TryGetValue(ip, out var semaphore))
        {
            semaphore = new SemaphoreSlim(1);
            IPLocks[ip] = semaphore;
        }

        try
        {
            await semaphore.WaitAsync(Context.RequestAborted).ConfigureAwait(false);
            var authResult = await secretKeyAuthenticatorService.AuthorizeAsync(ip, authHeader).ConfigureAwait(false);

            if (!authResult.Success)
            {
                return AuthenticateResult.Fail("Failed Authorization");
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, authResult.Uid),
            };

            var identity = new ClaimsIdentity(claims, nameof(SecretKeyAuthenticationHandler));
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return AuthenticateResult.Success(ticket);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
