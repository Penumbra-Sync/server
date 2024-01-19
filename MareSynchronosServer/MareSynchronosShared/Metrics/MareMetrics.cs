using Microsoft.Extensions.Logging;
using Prometheus;

namespace MareSynchronosShared.Metrics;

public class MareMetrics
{
    public MareMetrics(ILogger<MareMetrics> logger, List<string> countersToServe, List<string> gaugesToServe)
    {
        logger.LogInformation("Initializing MareMetrics");
        foreach (var counter in countersToServe)
        {
            logger.LogInformation($"Creating Metric for Counter {counter}");
            _counters.Add(counter, Prometheus.Metrics.CreateCounter(counter, counter));
        }

        foreach (var gauge in gaugesToServe)
        {
            logger.LogInformation($"Creating Metric for Counter {gauge}");
            if (!string.Equals(gauge, MetricsAPI.GaugeConnections, StringComparison.OrdinalIgnoreCase))
                _gauges.Add(gauge, Prometheus.Metrics.CreateGauge(gauge, gauge));
            else
                _gauges.Add(gauge, Prometheus.Metrics.CreateGauge(gauge, gauge, new[] { "continent" }));
        }
    }

    private readonly Dictionary<string, Counter> _counters = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Gauge> _gauges = new(StringComparer.Ordinal);

    public void IncGaugeWithLabels(string gaugeName, double value = 1.0, params string[] labels)
    {
        if (_gauges.TryGetValue(gaugeName, out Gauge gauge))
        {
            lock (gauge)
                gauge.WithLabels(labels).Inc(value);
        }
    }

    public void DecGaugeWithLabels(string gaugeName, double value = 1.0, params string[] labels)
    {
        if (_gauges.TryGetValue(gaugeName, out Gauge gauge))
        {
            lock (gauge)
                gauge.WithLabels(labels).Dec(value);
        }
    }

    public void SetGaugeTo(string gaugeName, double value)
    {
        if (_gauges.TryGetValue(gaugeName, out Gauge gauge))
        {
            lock (gauge)
                gauge.Set(value);
        }
    }

    public void IncGauge(string gaugeName, double value = 1.0)
    {
        if (_gauges.TryGetValue(gaugeName, out Gauge gauge))
        {
            lock (gauge)
                gauge.Inc(value);
        }
    }

    public void DecGauge(string gaugeName, double value = 1.0)
    {
        if (_gauges.TryGetValue(gaugeName, out Gauge gauge))
        {
            lock (gauge)
                gauge.Dec(value);
        }
    }

    public void IncCounter(string counterName, double value = 1.0)
    {
        if (_counters.TryGetValue(counterName, out Counter counter))
        {
            lock (counter)
                counter.Inc(value);
        }
    }
}