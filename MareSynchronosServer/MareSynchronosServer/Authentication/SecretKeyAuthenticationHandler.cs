using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using MareSynchronosServer.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MareSynchronosServer.Authentication
{
    public class SecretKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private readonly MareDbContext _mareDbContext;
        public const string AUTH_SCHEME = "SecretKeyAuth";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("Authorization"))
                return Task.FromResult(AuthenticateResult.Fail("Failed Authorization"));

            var authHeader = Request.Headers["Authorization"].ToString();

            if (string.IsNullOrEmpty(authHeader))
                return Task.FromResult(AuthenticateResult.Fail("Failed Authorization"));

            using var sha256 = SHA256.Create();
            var hashedHeader = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(authHeader))).Replace("-", "");
            var user = _mareDbContext.Users.SingleOrDefault(m => m.SecretKey == hashedHeader);

            if (user == null)
            {
                return Task.FromResult(AuthenticateResult.Fail("Failed Authorization"));
            }

            var claims = new List<Claim> {
                new Claim(ClaimTypes.Name, user.CharacterIdentification ?? "Unknown"),
                new Claim(ClaimTypes.NameIdentifier, user.UID)
            };

            var identity = new ClaimsIdentity(claims, nameof(SecretKeyAuthenticationHandler));
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, this.Scheme.Name);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        public SecretKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock, MareDbContext mareDbContext) : base(options, logger, encoder, clock)
        {
            _mareDbContext = mareDbContext;
        }
    }
}
