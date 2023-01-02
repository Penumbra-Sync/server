using System.Collections.Concurrent;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MareSynchronosShared.Authentication;

public class SecretKeyAuthenticatorService
{
    private readonly MareMetrics _metrics;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IConfigurationService<MareConfigurationAuthBase> _configurationService;
    private readonly ILogger<SecretKeyAuthenticatorService> _logger;
    private readonly ConcurrentDictionary<string, SecretKeyAuthReply> _cachedPositiveResponses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SecretKeyFailedAuthorization?> _failedAuthorizations = new(StringComparer.Ordinal);

    public SecretKeyAuthenticatorService(MareMetrics metrics, IServiceScopeFactory serviceScopeFactory, IConfigurationService<MareConfigurationAuthBase> configuration, ILogger<SecretKeyAuthenticatorService> logger)
    {
        _logger = logger;
        _configurationService = configuration;
        _metrics = metrics;
        _serviceScopeFactory = serviceScopeFactory;
    }

    public async Task<SecretKeyAuthReply> AuthorizeAsync(string ip, string hashedSecretKey)
    {
        _metrics.IncCounter(MetricsAPI.CounterAuthenticationRequests);

        if (_cachedPositiveResponses.TryGetValue(hashedSecretKey, out var cachedPositiveResponse))
        {
            _metrics.IncCounter(MetricsAPI.CounterAuthenticationCacheHits);
            return cachedPositiveResponse;
        }

        if (_failedAuthorizations.TryGetValue(ip, out var existingFailedAuthorization)
            && existingFailedAuthorization.FailedAttempts > _configurationService.GetValueOrDefault(nameof(MareConfigurationAuthBase.FailedAuthForTempBan), 5))
        {
            if (existingFailedAuthorization.ResetTask == null)
            {
                _logger.LogWarning("TempBan {ip} for authorization spam", ip);

                existingFailedAuthorization.ResetTask = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(_configurationService.GetValueOrDefault(nameof(MareConfigurationAuthBase.TempBanDurationInMinutes), 5))).ConfigureAwait(false);

                }).ContinueWith((t) =>
                {
                    _failedAuthorizations.Remove(ip, out _);
                });
            }
            return new(Success: false, Uid: null);
        }

        using var scope = _serviceScopeFactory.CreateScope();
        using var context = scope.ServiceProvider.GetService<MareDbContext>();
        var authReply = await context.Auth.AsNoTracking().SingleOrDefaultAsync(u => u.HashedKey == hashedSecretKey).ConfigureAwait(false);

        SecretKeyAuthReply reply = new(authReply != null, authReply?.UserUID);

        if (reply.Success)
        {
            _metrics.IncCounter(MetricsAPI.CounterAuthenticationSuccesses);

            _cachedPositiveResponses[hashedSecretKey] = reply;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                _cachedPositiveResponses.TryRemove(hashedSecretKey, out _);
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
        var whitelisted = _configurationService.GetValueOrDefault(nameof(MareConfigurationAuthBase.WhitelistedIps), new List<string>());
        if (!whitelisted.Any(w => ip.Contains(w, StringComparison.OrdinalIgnoreCase)))
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
