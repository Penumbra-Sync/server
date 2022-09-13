using MareSynchronosShared.Metrics;

namespace MareSynchronosShared.Services;

public class LocalClientIdentificationService : BaseClientIdentificationService
{
    public LocalClientIdentificationService(MareMetrics metrics) : base(metrics)
    {
    }
}