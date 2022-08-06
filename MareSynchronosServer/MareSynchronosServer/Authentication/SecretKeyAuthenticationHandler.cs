using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
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
        private const string unauthorized = "Unauthorized";
        public static ConcurrentDictionary<string, string> Authentications = new();
        private static SemaphoreSlim dbLockSemaphore = new SemaphoreSlim(20);

        public static void ClearUnauthorizedUsers()
        {
            foreach (var item in Authentications.ToArray())
            {
                if (item.Value == unauthorized)
                {
                    Authentications[item.Key] = string.Empty;
                }
            }
        }

        public static void RemoveAuthentication(string uid)
        {
            var auth = Authentications.Where(u => u.Value == uid);
            if (auth.Any())
            {
                Authentications.Remove(auth.First().Key, out _);
            }
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("Authorization"))
                return AuthenticateResult.Fail("Failed Authorization");

            var authHeader = Request.Headers["Authorization"].ToString();

            if (string.IsNullOrEmpty(authHeader))
                return AuthenticateResult.Fail("Failed Authorization");

            using var sha256 = SHA256.Create();
            var hashedHeader = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(authHeader))).Replace("-", "");

            if (Authentications.TryGetValue(hashedHeader, out string uid))
            {
                if (uid == unauthorized)
                    return AuthenticateResult.Fail("Failed Authorization");
                else
                    Logger.LogDebug("Found cached entry for " + uid);
            }

            if (string.IsNullOrEmpty(uid))
            {
                try
                {
                    await dbLockSemaphore.WaitAsync();
                    uid = (await _mareDbContext.Auth.Include("User").AsNoTracking()
                        .FirstOrDefaultAsync(m => m.HashedKey == hashedHeader))?.UserUID;
                }
                catch { }
                finally
                {
                    dbLockSemaphore.Release();
                }

                if (uid == null)
                {
                    Authentications[hashedHeader] = unauthorized;
                    return AuthenticateResult.Fail("Failed Authorization");
                }
                else
                {
                    Authentications[hashedHeader] = uid;
                }
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
