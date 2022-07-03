using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        public static ConcurrentDictionary<string, object> IdentificationLocks = new();
        private readonly MareDbContext _mareDbContext;
        public const string AuthScheme = "SecretKeyAuth";

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("Authorization") || !Request.Headers.ContainsKey("CharacterNameHash"))
                return AuthenticateResult.Fail("Failed Authorization");

            var authHeader = Request.Headers["Authorization"].ToString();
            var charNameHeader = Request.Headers["CharacterNameHash"].ToString();

            if (string.IsNullOrEmpty(authHeader) || string.IsNullOrEmpty(charNameHeader) || charNameHeader == "--")
                return AuthenticateResult.Fail("Failed Authorization");

            var isBanned = await _mareDbContext.BannedUsers.AnyAsync(u => u.CharacterIdentification == charNameHeader);
            if (isBanned)
            {
                return AuthenticateResult.Fail("Banned");
            }

            using var sha256 = SHA256.Create();
            var hashedHeader = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(authHeader))).Replace("-", "");
            var user = _mareDbContext.Users.SingleOrDefault(m => m.SecretKey == hashedHeader);

            if (user == null)
            {
                return AuthenticateResult.Fail("Failed Authorization");
            }

            if (!IdentificationLocks.TryGetValue(charNameHeader, out var lockObject))
            {
                lockObject = new();
                IdentificationLocks[charNameHeader] = lockObject;
            }

            if (user.CharacterIdentification != charNameHeader)
            {
                lock (lockObject)
                {
                    try
                    {
                        user.CharacterIdentification = charNameHeader;
                        _mareDbContext.Users.Update(user);
                        _mareDbContext.SaveChanges();
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                    }
                }
            }

            var claims = new List<Claim> {
                new Claim(ClaimTypes.Name, user.CharacterIdentification),
                new Claim(ClaimTypes.NameIdentifier, user.UID)
            };

            var identity = new ClaimsIdentity(claims, nameof(SecretKeyAuthenticationHandler));
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, this.Scheme.Name);

            return AuthenticateResult.Success(ticket);
        }

        public SecretKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock, MareDbContext mareDbContext) : base(options, logger, encoder, clock)
        {
            _mareDbContext = mareDbContext;
        }
    }
}
