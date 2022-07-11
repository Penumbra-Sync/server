using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using MareSynchronosServer.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MareSynchronosServer.Authentication
{
    public class SecretKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private readonly MareDbContext _mareDbContext;
        public const string AuthScheme = "SecretKeyAuth";

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("Authorization"))
                return AuthenticateResult.Fail("Failed Authorization");

            var authHeader = Request.Headers["Authorization"].ToString();

            if (string.IsNullOrEmpty(authHeader))
                return AuthenticateResult.Fail("Failed Authorization");

            using var sha256 = SHA256.Create();
            var hashedHeader = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(authHeader))).Replace("-", "");
            var uid = (await _mareDbContext.Users.AsNoTracking().FirstOrDefaultAsync(m => m.SecretKey == hashedHeader))?.UID;

            if (uid == null)
            {
                return AuthenticateResult.Fail("Failed Authorization");
            }

            var claims = new List<Claim> {
                new Claim(ClaimTypes.NameIdentifier, uid)
            };

            var identity = new ClaimsIdentity(claims, nameof(SecretKeyAuthenticationHandler));
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return AuthenticateResult.Success(ticket);
        }

        public SecretKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
            MareDbContext mareDbContext, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock) : base(options, logger, encoder, clock)
        {
            _mareDbContext = mareDbContext;
        }
    }
}
