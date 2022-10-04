using MareSynchronosServer.Hubs;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Utils;

public class MareHubLogger
{
    private readonly MareHub _hub;
    private readonly ILogger<MareHub> _logger;

    public MareHubLogger(MareHub hub, ILogger<MareHub> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public void LogCallInfo(string methodName, params object[] args)
    {
        string formattedArgs = args.Length != 0 ? "|" + string.Join(":", args) : string.Empty;
        _logger.LogInformation("{uid}:{method}{args}", _hub.AuthenticatedUserId, methodName, formattedArgs);
    }

    public void LogCallWarning(string methodName, params object[] args)
    {
        string formattedArgs = args.Length != 0 ? "|" + string.Join(":", args) : string.Empty;
        _logger.LogWarning("{uid}:{method}{args}", _hub.AuthenticatedUserId, methodName, formattedArgs);
    }
}
