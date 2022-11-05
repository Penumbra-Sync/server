using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Protos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServices.Authentication;

public class SecretKeyAuthenticationHandler
{
    private readonly ILogger<SecretKeyAuthenticationHandler> _logger;
    private readonly MareMetrics _metrics;
    private const string Unauthorized = "Unauthorized";
    private readonly ConcurrentDictionary<string, string> _cachedAuthorizations = new();
    private readonly ConcurrentDictionary<string, FailedAuthorization?> _failedAuthorizations = new();
    private readonly int _failedAttemptsForTempBan;
    private readonly int _tempBanMinutes;
    private readonly List<string> _whitelistedIps = new();

    public void ClearUnauthorizedUsers()
    {
        foreach (var item in _cachedAuthorizations.ToArray())
        {
            if (item.Value == Unauthorized)
            {
                _cachedAuthorizations[item.Key] = string.Empty;
            }
        }
    }

    public void RemoveAuthentication(string uid)
    {
        var authorization = _cachedAuthorizations.Where(u => u.Value == uid);
        if (authorization.Any())
        {
            _cachedAuthorizations.Remove(authorization.First().Key, out _);
        }
    }

    public async Task<AuthReply> AuthenticateAsync(MareDbContext mareDbContext, string ip, string secretKey)
    {
        _metrics.IncCounter(MetricsAPI.CounterAuthenticationRequests);

        if (string.IsNullOrEmpty(secretKey))
        {
            _metrics.IncCounter(MetricsAPI.CounterAuthenticationFailures);
            return new AuthReply() { Success = false, Uid = new UidMessage() { Uid = string.Empty } };
        }

        if (_failedAuthorizations.TryGetValue(ip, out var existingFailedAuthorization) && existingFailedAuthorization.FailedAttempts > _failedAttemptsForTempBan)
        {
            _metrics.IncCounter(MetricsAPI.CounterAuthenticationCacheHits);
            _metrics.IncCounter(MetricsAPI.CounterAuthenticationFailures);

            if (existingFailedAuthorization.ResetTask == null)
            {
                _logger.LogWarning("TempBan {ip} for authorization spam", ip);

                existingFailedAuthorization.ResetTask = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(_tempBanMinutes)).ConfigureAwait(false);

                }).ContinueWith((t) =>
                {
                    _failedAuthorizations.Remove(ip, out _);
                });
            }
            return new AuthReply() { Success = false, Uid = new UidMessage() { Uid = string.Empty } };
        }

        using var sha256 = SHA256.Create();
        var hashedHeader = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(secretKey))).Replace("-", "");

        bool fromCache = _cachedAuthorizations.TryGetValue(hashedHeader, out string uid);

        if (fromCache)
        {
            _metrics.IncCounter(MetricsAPI.CounterAuthenticationCacheHits);

            if (uid == Unauthorized)
            {
                return AuthenticationFailure(ip);
            }
        }
        else
        {
            uid = (await mareDbContext.Auth.AsNoTracking()
                .FirstOrDefaultAsync(m => m.HashedKey == hashedHeader).ConfigureAwait(false))?.UserUID;

            if (uid == null)
            {
                _cachedAuthorizations[hashedHeader] = Unauthorized;

                return AuthenticationFailure(ip);
            }

            _cachedAuthorizations[hashedHeader] = uid;
        }

        _metrics.IncCounter(MetricsAPI.CounterAuthenticationSuccesses);

        return new AuthReply() { Success = true, Uid = new UidMessage() { Uid = uid } };
    }

    private AuthReply AuthenticationFailure(string ip)
    {
        _metrics.IncCounter(MetricsAPI.CounterAuthenticationFailures);

        _logger.LogWarning("Failed authorization from {ip}", ip);
        if (!_whitelistedIps.Any(w => ip.Contains(w)))
        {
            if (_failedAuthorizations.TryGetValue(ip, out var auth))
            {
                auth.IncreaseFailedAttempts();
            }
            else
            {
                _failedAuthorizations[ip] = new FailedAuthorization();
            }
        }

        return new AuthReply() { Success = false, Uid = new UidMessage() { Uid = string.Empty } };
    }

    public SecretKeyAuthenticationHandler(IConfiguration configuration, ILogger<SecretKeyAuthenticationHandler> logger, MareMetrics metrics)
    {
        this._logger = logger;
        this._metrics = metrics;
        var config = configuration.GetRequiredSection("MareSynchronos");
        _failedAttemptsForTempBan = config.GetValue<int>("FailedAuthForTempBan", 5);
        logger.LogInformation("FailedAuthForTempBan: {num}", _failedAttemptsForTempBan);
        _tempBanMinutes = config.GetValue<int>("TempBanDurationInMinutes", 30);
        logger.LogInformation("TempBanMinutes: {num}", _tempBanMinutes);
        var whitelisted = config.GetSection("WhitelistedIps");
        if (!string.IsNullOrEmpty(whitelisted.Value))
        {
            _whitelistedIps = whitelisted.Get<List<string>>();
            foreach (var ip in _whitelistedIps)
            {
                logger.LogInformation("Whitelisted IP: " + ip);
            }
        }
    }
}