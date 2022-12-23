using System.Collections.Concurrent;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MareSynchronosShared.Authentication;

public class SecretKeyAuthenticatorService
{
    private readonly MareMetrics _metrics;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<SecretKeyAuthenticatorService> _logger;
    private readonly ConcurrentDictionary<string, SecretKeyAuthReply> _cachedPositiveResponses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SecretKeyFailedAuthorization?> _failedAuthorizations = new(StringComparer.Ordinal);
    private readonly int _failedAttemptsForTempBan;
    private readonly int _tempBanMinutes;
    private readonly List<string> _whitelistedIps;

    public SecretKeyAuthenticatorService(MareMetrics metrics, IServiceScopeFactory serviceScopeFactory, IOptions<MareConfigurationAuthBase> configuration, ILogger<SecretKeyAuthenticatorService> logger)
    {
        _logger = logger;
        var config = configuration.Value;
        _failedAttemptsForTempBan = config.FailedAuthForTempBan;
        _tempBanMinutes = config.TempBanDurationInMinutes;
        _whitelistedIps = config.WhitelistedIps;
        foreach (var ip in _whitelistedIps)
        {
            logger.LogInformation("Whitelisted IP: " + ip);
        }

        _metrics = metrics;
        _serviceScopeFactory = serviceScopeFactory;
    }

    internal async Task<SecretKeyAuthReply> AuthorizeAsync(string ip, string secretKey)
    {
        _metrics.IncCounter(MetricsAPI.CounterAuthenticationRequests);

        if (_cachedPositiveResponses.TryGetValue(secretKey, out var cachedPositiveResponse))
        {
            _metrics.IncCounter(MetricsAPI.CounterAuthenticationCacheHits);
            return cachedPositiveResponse;
        }

        if (_failedAuthorizations.TryGetValue(ip, out var existingFailedAuthorization) && existingFailedAuthorization.FailedAttempts > _failedAttemptsForTempBan)
        {
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
            return new(Success: false, Uid: null);
        }

        using var scope = _serviceScopeFactory.CreateScope();
        using var context = scope.ServiceProvider.GetService<MareDbContext>();
        var hashedHeader = StringUtils.Sha256String(secretKey);
        var authReply = await context.Auth.AsNoTracking().SingleOrDefaultAsync(u => u.HashedKey == hashedHeader).ConfigureAwait(false);

        SecretKeyAuthReply reply = new(authReply != null, authReply?.UserUID);

        if (reply.Success)
        {
            _metrics.IncCounter(MetricsAPI.CounterAuthenticationSuccesses);

            _cachedPositiveResponses[secretKey] = reply;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                _cachedPositiveResponses.TryRemove(secretKey, out _);
            });

        }
        else
        {
            return AuthenticationFailure(ip);
        }

        return reply;
    }

    private SecretKeyAuthReply AuthenticationFailure(string ip)
    {
        _metrics.IncCounter(MetricsAPI.CounterAuthenticationFailures);

        _logger.LogWarning("Failed authorization from {ip}", ip);
        if (!_whitelistedIps.Any(w => ip.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            if (_failedAuthorizations.TryGetValue(ip, out var auth))
            {
                auth.IncreaseFailedAttempts();
            }
            else
            {
                _failedAuthorizations[ip] = new SecretKeyFailedAuthorization();
            }
        }

        return new(Success: false, Uid: null);
    }
}
