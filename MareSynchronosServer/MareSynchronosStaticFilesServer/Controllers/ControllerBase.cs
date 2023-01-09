using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Controllers;

public class ControllerBase : Controller
{
    protected ILogger _logger;
    public ControllerBase(ILogger logger)
    {
        _logger = logger;
    }

    protected string User => HttpContext.User.Claims.First(f => string.Equals(f.Type, MareClaimTypes.Uid, StringComparison.Ordinal)).Value;
    protected string Authorization => Request.Headers["Authorization"];
}
