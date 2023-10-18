using System.Collections.Concurrent;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Authentication;

public class SecretKeyAuthenticatorService
{
    private readonly MareMetrics _metrics;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IConfigurationService<MareConfigurationAuthBase> _configurationService;
    private readonly ILogger<SecretKeyAuthenticatorService> _logger;
    private readonly ConcurrentDictionary<string, SecretKeyAuthReply> _cachedPositiveResponses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SecretKeyFailedAuthorization> _failedAuthorizations = new(StringComparer.Ordinal);

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
            return new(Success: false, Uid: null, PrimaryUid: null, TempBan: true, Permaban: false);
        }

        using var scope = _serviceScopeFactory.CreateScope();
        using var context = scope.ServiceProvider.GetService<MareDbContext>();
        var authReply = await context.Auth.AsNoTracking().SingleOrDefaultAsync(u => u.HashedKey == hashedSecretKey).ConfigureAwait(false);
        var isBanned = authReply?.IsBanned ?? false;
        var primaryUid = authReply?.PrimaryUserUID ?? authReply?.UserUID;

        if (authReply?.PrimaryUserUID != null)
        {
            var primaryUser = await context.Auth.AsNoTracking().SingleOrDefaultAsync(u => u.UserUID == authReply.PrimaryUserUID).ConfigureAwait(false);
            isBanned = isBanned || primaryUser.IsBanned;
        }

        SecretKeyAuthReply reply = new(authReply != null, authReply?.UserUID, authReply?.PrimaryUserUID ?? authReply?.UserUID, TempBan: false, isBanned);

        if (reply.Success)
        {
            _metrics.IncCounter(MetricsAPI.CounterAuthenticationSuccesses);
            _metrics.IncGauge(MetricsAPI.GaugeAuthenticationCacheEntries);

            _cachedPositiveResponses[hashedSecretKey] = reply;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                _cachedPositiveResponses.TryRemove(hashedSecretKey, out _);
                _metrics.DecGauge(MetricsAPI.GaugeAuthenticationCacheEntries);
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
        if (!whitelisted.Exists(w => ip.Contains(w, StringComparison.OrdinalIgnoreCase)))
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

        return new(Success: false, Uid: null, PrimaryUid: null, TempBan: false, Permaban: false);
    }
}
