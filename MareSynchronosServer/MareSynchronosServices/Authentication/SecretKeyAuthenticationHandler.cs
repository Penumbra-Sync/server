using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Protos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServices.Authentication;

internal class SecretKeyAuthenticationHandler
{
    private readonly ILogger<SecretKeyAuthenticationHandler> logger;
    private readonly MareMetrics metrics;
    private const string Unauthorized = "Unauthorized";
    private readonly Dictionary<string, string> authorizations = new();
    private readonly Dictionary<string, FailedAuthorization?> failedAuthorizations = new();
    private readonly object authDictLock = new();
    private readonly object failedAuthLock = new();
    private readonly int _failedAttemptsForTempBan;
    private readonly int _tempBanMinutes;
    private List<string> _whitelistedIps = new();

    public void ClearUnauthorizedUsers()
    {
        lock (authDictLock)
        {
            foreach (var item in authorizations.ToArray())
            {
                if (item.Value == Unauthorized)
                {
                    authorizations[item.Key] = string.Empty;
                }
            }
        }
    }

    public void RemoveAuthentication(string uid)
    {
        lock (authDictLock)
        {
            var authorization = authorizations.Where(u => u.Value == uid);
            if (authorization.Any())
            {
                authorizations.Remove(authorization.First().Key);
            }
        }
    }

    public async Task<AuthReply> AuthenticateAsync(MareDbContext mareDbContext, string ip, string secretKey)
    {
        metrics.IncCounter(MetricsAPI.CounterAuthenticationRequests);

        if (string.IsNullOrEmpty(secretKey))
        {
            metrics.IncCounter(MetricsAPI.CounterAuthenticationFailures);
            return new AuthReply() { Success = false, Uid = new UidMessage() { Uid = string.Empty } };
        }

        lock (failedAuthLock)
        {
            if (failedAuthorizations.TryGetValue(ip, out var existingFailedAuthorization) && existingFailedAuthorization.FailedAttempts > _failedAttemptsForTempBan)
            {
                metrics.IncCounter(MetricsAPI.CounterAuthenticationFailures);

                existingFailedAuthorization.ResetCts?.Cancel();
                existingFailedAuthorization.ResetCts?.Dispose();
                existingFailedAuthorization.ResetCts = new CancellationTokenSource();
                var token = existingFailedAuthorization.ResetCts.Token;
                existingFailedAuthorization.ResetTask = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(_tempBanMinutes), token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) return;
                    FailedAuthorization? failedAuthorization;
                    lock (failedAuthLock)
                    {
                        failedAuthorizations.Remove(ip, out failedAuthorization);
                    }
                    failedAuthorization?.Dispose();
                }, token);

                logger.LogWarning("TempBan {ip} for authorization spam", ip);
                return new AuthReply() { Success = false, Uid = new UidMessage() { Uid = string.Empty } };
            }
        }

        using var sha256 = SHA256.Create();
        var hashedHeader = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(secretKey))).Replace("-", "");

        string uid;
        lock (authDictLock)
        {
            if (authorizations.TryGetValue(hashedHeader, out uid))
            {
                if (uid == Unauthorized)
                {
                    metrics.IncCounter(MetricsAPI.CounterAuthenticationFailures);

                    lock (failedAuthLock)
                    {
                        logger.LogWarning("Failed authorization from {ip}", ip);
                        if (failedAuthorizations.TryGetValue(ip, out var auth))
                        {
                            auth.IncreaseFailedAttempts();
                        }
                        else
                        {
                            failedAuthorizations[ip] = new FailedAuthorization();
                        }
                    }

                    return new AuthReply() { Success = false, Uid = new UidMessage() { Uid = string.Empty } };
                }

                metrics.IncCounter(MetricsAPI.CounterAuthenticationCacheHits);
            }
        }

        if (string.IsNullOrEmpty(uid))
        {
            uid = (await mareDbContext.Auth.AsNoTracking()
                .FirstOrDefaultAsync(m => m.HashedKey == hashedHeader).ConfigureAwait(false))?.UserUID;

            if (uid == null)
            {
                lock (authDictLock)
                {
                    authorizations[hashedHeader] = Unauthorized;
                }

                logger.LogWarning("Failed authorization from {ip}", ip);
                lock (failedAuthLock)
                {
                    if (!_whitelistedIps.Any(w => ip.Contains(w)))
                    {
                        if (failedAuthorizations.TryGetValue(ip, out var auth))
                        {
                            auth.IncreaseFailedAttempts();
                        }
                        else
                        {
                            failedAuthorizations[ip] = new FailedAuthorization();
                        }
                    }

                }

                metrics.IncCounter(MetricsAPI.CounterAuthenticationFailures);
                return new AuthReply() { Success = false, Uid = new UidMessage() { Uid = string.Empty } };
            }

            lock (authDictLock)
            {
                authorizations[hashedHeader] = uid;
            }
        }

        metrics.IncCounter(MetricsAPI.CounterAuthenticationSuccesses);

        return new AuthReply() { Success = true, Uid = new UidMessage() { Uid = uid } };
    }

    public SecretKeyAuthenticationHandler(IConfiguration configuration, ILogger<SecretKeyAuthenticationHandler> logger, MareMetrics metrics)
    {
        this.logger = logger;
        this.metrics = metrics;
        var config = configuration.GetRequiredSection("MareSynchronos");
        _failedAttemptsForTempBan = config.GetValue<int>("FailedAuthForTempBan", 5);
        _tempBanMinutes = config.GetValue<int>("TempBanDurationInMinutes", 30);
        _whitelistedIps = config.GetValue<List<string>>("WhitelistedIps", new List<string>());
    }
}