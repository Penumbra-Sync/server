using Prometheus;

namespace MareSynchronosServer.Metrics;

public class LockedProxyGauge
{
    private readonly Gauge _g;

    public LockedProxyGauge(Gauge g)
    {
        _g = g;
    }

    public void Inc(double inc = 1d)
    {
        //lock (_g)
        //{
            _g.Inc(inc);
        //}
    }

    public void IncTo(double incTo)
    {
        //lock (_g)
        //{
            _g.IncTo(incTo);
        //}
    }

    public void Dec(double decBy = 1d)
    {
        //lock (_g)
        //{
            _g.Dec(decBy);
        //}
    }

    public void Set(double setTo)
    {
        //lock (_g)
        //{
            _g.Set(setTo);
        //}
    }

    public double Value =>  _g.Value;
}