using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;

using Prometheus;

namespace MareSynchronosServices.Metrics;

public class MareMetrics
{
    public MareMetrics(IServiceProvider services, IConfiguration configuration)
    {
        using var scope = services.CreateScope();
        using var dbContext = scope.ServiceProvider.GetService<MareDbContext>();

        gauges[MetricsAPI.GaugeUsersRegistered].IncTo(dbContext.Users.Count());
        gauges[MetricsAPI.GaugePairs].IncTo(dbContext.ClientPairs.Count());
        gauges[MetricsAPI.GaugePairsPaused].IncTo(dbContext.ClientPairs.Count(p => p.IsPaused));
        gauges[MetricsAPI.GaugeFilesTotal].IncTo(dbContext.Files.Count());
        gauges[MetricsAPI.GaugeFilesTotalSize].IncTo(Directory.EnumerateFiles(configuration["CacheDirectory"]).Sum(f => new FileInfo(f).Length));
    }

    private readonly Dictionary<string, Counter> counters = new()
    {
        { MetricsAPI.CounterInitializedConnections, Prometheus.Metrics.CreateCounter(MetricsAPI.CounterInitializedConnections, "Initialized Connections") },
        { MetricsAPI.CounterUserPushData, Prometheus.Metrics.CreateCounter(MetricsAPI.CounterUserPushData, "Users pushing data") },
        { MetricsAPI.CounterUserPushDataTo, Prometheus.Metrics.CreateCounter(MetricsAPI.CounterUserPushDataTo, "Users Receiving Data") },
        { MetricsAPI.CounterAuthenticationRequests, Prometheus.Metrics.CreateCounter(MetricsAPI.CounterAuthenticationRequests, "Authentication Requests") },
        { MetricsAPI.CounterAuthenticationCacheHits, Prometheus.Metrics.CreateCounter(MetricsAPI.CounterAuthenticationCacheHits, "Authentication Requests Cache Hits") },
        { MetricsAPI.CounterAuthenticationFailures, Prometheus.Metrics.CreateCounter(MetricsAPI.CounterAuthenticationFailures, "Authentication Requests Failed") },
        { MetricsAPI.CounterAuthenticationSuccesses, Prometheus.Metrics.CreateCounter(MetricsAPI.CounterAuthenticationSuccesses, "Authentication Requests Success") },
    };

    private readonly Dictionary<string, Gauge> gauges = new()
    {
        { MetricsAPI.GaugeConnections, Prometheus.Metrics.CreateGauge(MetricsAPI.GaugeConnections, "Unauthorized Connections") },
        { MetricsAPI.GaugeAuthorizedConnections, Prometheus.Metrics.CreateGauge(MetricsAPI.GaugeConnections, "Authorized Connections") },
        { MetricsAPI.GaugeAvailableIOWorkerThreads, Prometheus.Metrics.CreateGauge(MetricsAPI.GaugeConnections, "Available Threadpool IO Workers") },
        { MetricsAPI.GaugeAvailableWorkerThreads, Prometheus.Metrics.CreateGauge(MetricsAPI.GaugeConnections, "Aavailable Threadpool Workers") },
        { MetricsAPI.GaugeUsersRegistered, Prometheus.Metrics.CreateGauge(MetricsAPI.GaugeConnections, "Total Registrations") },
        { MetricsAPI.GaugePairs, Prometheus.Metrics.CreateGauge(MetricsAPI.GaugeConnections, "Total Pairs") },
        { MetricsAPI.GaugePairsPaused, Prometheus.Metrics.CreateGauge(MetricsAPI.GaugeConnections, "Total Paused Pairs") },
        { MetricsAPI.GaugeFilesTotal, Prometheus.Metrics.CreateGauge(MetricsAPI.GaugeConnections, "Total uploaded files") },
        { MetricsAPI.GaugeFilesTotalSize, Prometheus.Metrics.CreateGauge(MetricsAPI.GaugeConnections, "Total uploaded files (bytes)") },
    };

    public void SetGaugeTo(string gaugeName, double value)
    {
        if (gauges.ContainsKey(gaugeName))
        {
            gauges[gaugeName].IncTo(value);
        }
    }

    public void IncGaugeBy(string gaugeName, double value)
    {
        if (gauges.ContainsKey(gaugeName))
        {
            gauges[gaugeName].Inc(value);
        }
    }

    public void DecGaugeBy(string gaugeName, double value)
    {
        if (gauges.ContainsKey(gaugeName))
        {
            gauges[gaugeName].Dec(value);
        }
    }

    public void IncCounter(string counterName)
    {
        IncCounterBy(counterName, 1);
    }

    public void IncCounterBy(string counterName, double value)
    {
        if (counters.ContainsKey(counterName))
        {
            counters[counterName].Inc(value);
        }
    }
}