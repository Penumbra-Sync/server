using MareSynchronosServer.Hubs;
using System.Runtime.CompilerServices;

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

    public static object[] Args(params object[] args)
    {
        return args;
    }

    public void LogCallInfo(object[] args = null, [CallerMemberName] string methodName = "")
    {
        string formattedArgs = args != null && args.Length != 0 ? "|" + string.Join(":", args) : string.Empty;
        _logger.LogInformation("{uid}:{method}{args}", _hub.UserUID, methodName, formattedArgs);
    }

    public void LogCallWarning(object[] args = null, [CallerMemberName] string methodName = "")
    {
        string formattedArgs = args != null && args.Length != 0 ? "|" + string.Join(":", args) : string.Empty;
        _logger.LogWarning("{uid}:{method}{args}", _hub.UserUID, methodName, formattedArgs);
    }
}
