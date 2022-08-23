using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MareSynchronosServices.Metrics;

public class MareMetrics
{
    public MareMetrics(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        using var dbContext = scope.ServiceProvider.GetService<MareDbContext>();

        gauges[MetricsAPI.GaugeUsersRegistered].IncTo(dbContext.Users.Count());
        gauges[MetricsAPI.GaugePairs].IncTo(dbContext.ClientPairs.Count());
        gauges[MetricsAPI.GaugePairsPaused].IncTo(dbContext.ClientPairs.Count(p => p.IsPaused));
        gauges[MetricsAPI.GaugeFilesTotal].IncTo(dbContext.Files.Count());
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
        { MetricsAPI.GaugeAuthorizedConnections, Prometheus.Metrics.CreateGauge(MetricsAPI.GaugeAuthorizedConnections, "Authorized Connections") },
        { MetricsAPI.GaugeAvailableIOWorkerThreads, Prometheus.Metrics.CreateGauge(MetricsAPI.GaugeAvailableIOWorkerThreads, "Available Threadpool IO Workers") },
        { MetricsAPI.GaugeAvailableWorkerThreads, Prometheus.Metrics.CreateGauge(MetricsAPI.GaugeAvailableWorkerThreads, "Aavailable Threadpool Workers") },
        { MetricsAPI.GaugeUsersRegistered, Prometheus.Metrics.CreateGauge(MetricsAPI.GaugeUsersRegistered, "Total Registrations") },
        { MetricsAPI.GaugePairs, Prometheus.Metrics.CreateGauge(MetricsAPI.GaugePairs, "Total Pairs") },
        { MetricsAPI.GaugePairsPaused, Prometheus.Metrics.CreateGauge(MetricsAPI.GaugePairsPaused, "Total Paused Pairs") },
        { MetricsAPI.GaugeFilesTotal, Prometheus.Metrics.CreateGauge(MetricsAPI.GaugeFilesTotal, "Total uploaded files") },
        { MetricsAPI.GaugeFilesTotalSize, Prometheus.Metrics.CreateGauge(MetricsAPI.GaugeFilesTotalSize, "Total uploaded files (bytes)") },
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