using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Controllers;

public class ControllerBase : Controller
{
    protected ILogger _logger;
    private readonly ServerTokenGenerator _generator;

    public ControllerBase(ILogger logger, ServerTokenGenerator generator)
    {
        _logger = logger;
        _generator = generator;
    }

    protected string MareUser => HttpContext.User.Claims.First(f => string.Equals(f.Type, MareClaimTypes.Uid, StringComparison.Ordinal)).Value;
    protected string Authorization => _generator.Token;
}
