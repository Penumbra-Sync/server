using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using MareSynchronosServer;
using MareSynchronosServer.Metrics;
using MareSynchronosShared.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ISystemClock = Microsoft.AspNetCore.Authentication.ISystemClock;

namespace MareSynchronosShared.Authentication
{
    public class FailedAuthorization : IDisposable
    {
        private int failedAttempts = 1;
        public int FailedAttempts => failedAttempts;
        public Task ResetTask { get; set; }
        public CancellationTokenSource? ResetCts { get; set; }

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
        public static readonly Dictionary<string, string> Authentications = new();
        private static readonly Dictionary<string, FailedAuthorization> FailedAuthorizations = new();
        private static readonly object authDictLock = new();
        private static readonly object failedAuthLock = new();
        private readonly int failedAttemptsForTempBan;
        private readonly int tempBanMinutes;

        public static void ClearUnauthorizedUsers()
        {
            lock (authDictLock)
            {
                foreach (var item in Authentications.ToArray())
                {
                    if (item.Value == unauthorized)
                    {
                        Authentications[item.Key] = string.Empty;
                    }
                }
            }
        }

        public static void RemoveAuthentication(string uid)
        {
            lock (authDictLock)
            {
                var auth = Authentications.Where(u => u.Value == uid);
                if (auth.Any())
                {
                    Authentications.Remove(auth.First().Key);
                }
            }
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            MareMetrics.AuthenticationRequests.Inc();

            if (!Request.Headers.ContainsKey("Authorization"))
            {
                MareMetrics.AuthenticationFailures.Inc();
                return AuthenticateResult.Fail("Failed Authorization");
            }

            var authHeader = Request.Headers["Authorization"].ToString();

            if (string.IsNullOrEmpty(authHeader))
            {
                MareMetrics.AuthenticationFailures.Inc();
                return AuthenticateResult.Fail("Failed Authorization");
            }

            var ip = _accessor.GetIpAddress();

            lock (failedAuthLock)
            {
                if (FailedAuthorizations.TryGetValue(ip, out var failedAuth) && failedAuth.FailedAttempts > failedAttemptsForTempBan)
                {
                    MareMetrics.AuthenticationFailures.Inc();

                    failedAuth.ResetCts?.Cancel();
                    failedAuth.ResetCts?.Dispose();
                    failedAuth.ResetCts = new CancellationTokenSource();
                    var token = failedAuth.ResetCts.Token;
                    failedAuth.ResetTask = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromMinutes(tempBanMinutes), token).ConfigureAwait(false);
                        if (token.IsCancellationRequested) return;
                        FailedAuthorization fauth;
                        lock (failedAuthLock)
                        {
                            FailedAuthorizations.Remove(ip, out fauth);
                        }
                        fauth.Dispose();
                    }, token);

                    Logger.LogWarning("TempBan {ip} for authorization spam", ip);
                    return AuthenticateResult.Fail("Failed Authorization");
                }
            }

            using var sha256 = SHA256.Create();
            var hashedHeader = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(authHeader))).Replace("-", "");

            string uid;
            lock (authDictLock)
            {
                if (Authentications.TryGetValue(hashedHeader, out uid))
                {
                    if (uid == unauthorized)
                    {
                        MareMetrics.AuthenticationFailures.Inc();

                        lock (failedAuthLock)
                        {
                            Logger.LogWarning("Failed authorization from {ip}", ip);
                            if (FailedAuthorizations.TryGetValue(ip, out var auth))
                            {
                                auth.IncreaseFailedAttempts();
                            }
                            else
                            {
                                FailedAuthorizations[ip] = new FailedAuthorization();
                            }
                        }

                        return AuthenticateResult.Fail("Failed Authorization");
                    }

                    MareMetrics.AuthenticationCacheHits.Inc();
                }
            }

            if (string.IsNullOrEmpty(uid))
            {
                uid = (await _mareDbContext.Auth.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.HashedKey == hashedHeader).ConfigureAwait(false))?.UserUID;

                if (uid == null)
                {
                    lock (authDictLock)
                    {
                        Authentications[hashedHeader] = unauthorized;
                    }

                    Logger.LogWarning("Failed authorization from {ip}", ip);
                    lock (failedAuthLock)
                    {
                        if (FailedAuthorizations.TryGetValue(ip, out var auth))
                        {
                            auth.IncreaseFailedAttempts();
                        }
                        else
                        {
                            FailedAuthorizations[ip] = new FailedAuthorization();
                        }
                    }

                    MareMetrics.AuthenticationFailures.Inc();
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

            MareMetrics.AuthenticationSuccesses.Inc();

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
