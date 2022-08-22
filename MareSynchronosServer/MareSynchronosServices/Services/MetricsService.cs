using Grpc.Core;
using MareSynchronosServices.Metrics;
using MareSynchronosShared.Protos;

namespace MareSynchronosServices.Services;

public class MetricsService : MareSynchronosShared.Protos.MetricsService.MetricsServiceBase
{
    private readonly MareMetrics metrics;

    public MetricsService(MareMetrics metrics)
    {
        this.metrics = metrics;
    }

    public override Task<Empty> IncreaseCounter(IncreaseCounterRequest request, ServerCallContext context)
    {
        metrics.IncCounterBy(request.CounterName, request.Value);
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> SetGauge(SetGaugeRequest request, ServerCallContext context)
    {
        metrics.SetGaugeTo(request.GaugeName, request.Value);
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> DecGauge(GaugeRequest request, ServerCallContext context)
    {
        metrics.DecGaugeBy(request.GaugeName, request.Value);
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> IncGauge(GaugeRequest request, ServerCallContext context)
    {
        metrics.IncGaugeBy(request.GaugeName, request.Value);
        return Task.FromResult(new Empty());
    }
}