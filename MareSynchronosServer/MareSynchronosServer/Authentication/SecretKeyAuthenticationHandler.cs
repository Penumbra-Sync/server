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
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MareSynchronosServer.Authentication
{
    public class FailedAuthorization : IDisposable
    {
        private int failedAttempts = 1;
        public int FailedAttempts => failedAttempts;
        public Task ResetTask { get; set; }
        public CancellationTokenSource ResetCts { get; set; } = new();

        public void Dispose()
        {
            try
            {
                ResetCts?.Cancel();
                ResetCts?.Dispose();
            }
            catch { }
        }

        public void IncreaseFailedAttempts()
        {
            Interlocked.Increment(ref failedAttempts);
        }
    }

    public class SecretKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private readonly IHttpContextAccessor _accessor;
        private readonly MareDbContext _mareDbContext;
        private readonly IConfiguration _configuration;
        public const string AuthScheme = "SecretKeyAuth";
        private const string unauthorized = "Unauthorized";
        public static ConcurrentDictionary<string, string> Authentications = new();
        private static ConcurrentDictionary<string, FailedAuthorization> FailedAuthorizations = new();
        private static SemaphoreSlim dbLockSemaphore = new SemaphoreSlim(20);
        private int failedAttemptsForTempBan;
        private int tempBanMinutes;

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
            {
                return AuthenticateResult.Fail("Failed Authorization");
            }

            var authHeader = Request.Headers["Authorization"].ToString();

            if (string.IsNullOrEmpty(authHeader))
                return AuthenticateResult.Fail("Failed Authorization");

            var ip = _accessor.GetIpAddress();

            if (FailedAuthorizations.TryGetValue(ip, out var failedAuth))
            {
                if (failedAuth.FailedAttempts > failedAttemptsForTempBan)
                {
                    failedAuth.ResetCts.Cancel();
                    failedAuth.ResetCts.Dispose();
                    failedAuth.ResetCts = new CancellationTokenSource();
                    var token = failedAuth.ResetCts.Token;
                    failedAuth.ResetTask = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromMinutes(tempBanMinutes), token);
                        if (token.IsCancellationRequested) return;
                        FailedAuthorizations.Remove(ip, out var fauth);
                        fauth.Dispose();
                    }, token);
                    Logger.LogWarning("TempBan " + ip + " for authorization spam");
                    return AuthenticateResult.Fail("Failed Authorization");
                }
            }

            using var sha256 = SHA256.Create();
            var hashedHeader = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(authHeader))).Replace("-", "");

            if (Authentications.TryGetValue(hashedHeader, out string uid))
            {
                if (uid == unauthorized)
                {
                    Logger.LogWarning("Failed authorization from " + ip);
                    if (FailedAuthorizations.TryGetValue(ip, out var auth))
                    {
                        auth.IncreaseFailedAttempts();
                    }
                    else
                    {
                        FailedAuthorizations[ip] = new FailedAuthorization();
                    }
                    return AuthenticateResult.Fail("Failed Authorization");
                }
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
                    Logger.LogWarning("Failed authorization from " + ip);
                    if (FailedAuthorizations.TryGetValue(ip, out var auth))
                    {
                        auth.IncreaseFailedAttempts();
                    }
                    else
                    {
                        FailedAuthorizations[ip] = new FailedAuthorization();
                    }
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

        public SecretKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, IHttpContextAccessor accessor,
            MareDbContext mareDbContext, IConfiguration configuration, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock) : base(options, logger, encoder, clock)
        {
            _accessor = accessor;
            _mareDbContext = mareDbContext;
            _configuration = configuration;
            failedAttemptsForTempBan = _configuration.GetValue<int>("FailedAuthForTempBan", 5);
            tempBanMinutes = _configuration.GetValue<int>("TempBanDurationInMinutes", 30);
        }
    }
}
