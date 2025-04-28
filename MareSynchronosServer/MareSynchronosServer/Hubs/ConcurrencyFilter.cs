using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.AspNetCore.SignalR;

namespace MareSynchronosServer.Hubs;

public sealed class ConcurrencyFilter : IHubFilter, IDisposable
{
    private SemaphoreSlim _limiter;
    private int _setLimit = 0;
    private readonly IConfigurationService<ServerConfiguration> _config;
    private readonly CancellationTokenSource _cancellationToken;

    private bool _disposed;

    public ConcurrencyFilter(IConfigurationService<ServerConfiguration> config, MareMetrics mareMetrics)
    {
        _config = config;
        _config.ConfigChangedEvent += OnConfigChange;

        RecreateSemaphore();

        _ = Task.Run(async () =>
        {
            var token = _cancellationToken.Token;
            while (!token.IsCancellationRequested)
            {
                mareMetrics.SetGaugeTo(MetricsAPI.GaugeHubConcurrency, _limiter?.CurrentCount ?? 0);
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        });
    }

    private void OnConfigChange(object sender, EventArgs e)
    {
        RecreateSemaphore();
    }

    private void RecreateSemaphore()
    {
        var newLimit = _config.GetValueOrDefault(nameof(ServerConfiguration.HubExecutionConcurrencyFilter), 50);
        if (newLimit != _setLimit)
        {
            _setLimit = newLimit;
            _limiter?.Dispose();
            _limiter = new(initialCount: _setLimit, maxCount: _setLimit);
        }
    }

    public async ValueTask<object> InvokeMethodAsync(
    HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object>> next)
    {
        await _limiter.WaitAsync(invocationContext.Context.ConnectionAborted).ConfigureAwait(false);

        try
        {
            return await next(invocationContext).ConfigureAwait(false);
        }
        finally
        {
            _limiter.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellationToken.Cancel();
        _config.ConfigChangedEvent -= OnConfigChange;
    }
}
