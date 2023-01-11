using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Controllers;

public class ControllerBase : Controller
{
    protected ILogger _logger;
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configuration;

    public ControllerBase(ILogger logger, IConfigurationService<StaticFilesServerConfiguration> configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected string User => HttpContext.User.Claims.First(f => string.Equals(f.Type, MareClaimTypes.Uid, StringComparison.Ordinal)).Value;
    protected string Authorization => "Bearer " + _configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.JwtServerToken));
}
