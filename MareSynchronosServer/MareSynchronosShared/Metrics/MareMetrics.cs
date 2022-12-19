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
            counters.Add(counter, Prometheus.Metrics.CreateCounter(counter, counter));
        }

        foreach (var gauge in gaugesToServe)
        {
            logger.LogInformation($"Creating Metric for Counter {gauge}");
            gauges.Add(gauge, Prometheus.Metrics.CreateGauge(gauge, gauge));
        }
    }

    private readonly Dictionary<string, Counter> counters = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Gauge> gauges = new(StringComparer.Ordinal);

    public void SetGaugeTo(string gaugeName, double value)
    {
        if (gauges.ContainsKey(gaugeName))
        {
            gauges[gaugeName].IncTo(value);
        }
    }

    public void IncGauge(string gaugeName, double value = 1.0)
    {
        if (gauges.ContainsKey(gaugeName))
        {
            gauges[gaugeName].Inc(value);
        }
    }

    public void DecGauge(string gaugeName, double value = 1.0)
    {
        if (gauges.ContainsKey(gaugeName))
        {
            gauges[gaugeName].Dec(value);
        }
    }

    public void IncCounter(string counterName, double value = 1.0)
    {
        if (counters.ContainsKey(counterName))
        {
            counters[counterName].Inc(value);
        }
    }
}