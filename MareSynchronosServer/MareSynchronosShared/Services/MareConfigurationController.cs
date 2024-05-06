using MareSynchronosShared.Utils.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MareSynchronosShared.Services;

[Route("configuration/[controller]")]
[Authorize(Policy = "Internal")]
public class MareConfigurationController<T> : Controller where T : class, IMareConfiguration
{
    private readonly ILogger<MareConfigurationController<T>> _logger;
    private IOptionsMonitor<T> _config;

    public MareConfigurationController(IOptionsMonitor<T> config, ILogger<MareConfigurationController<T>> logger)
    {
        _config = config;
        _logger = logger;
    }

    [HttpGet("GetConfigurationEntry")]
    [Authorize(Policy = "Internal")]
    public IActionResult GetConfigurationEntry(string key, string defaultValue)
    {
        var result = _config.CurrentValue.SerializeValue(key, defaultValue);
        _logger.LogInformation("Requested " + key + ", returning:" + result);
        return Ok(result);
    }
}

#pragma warning disable MA0048 // File name must match type name
public class MareStaticFilesServerConfigurationController : MareConfigurationController<StaticFilesServerConfiguration>
{
    public MareStaticFilesServerConfigurationController(IOptionsMonitor<StaticFilesServerConfiguration> config, ILogger<MareStaticFilesServerConfigurationController> logger) : base(config, logger)
    {
    }
}

public class MareBaseConfigurationController : MareConfigurationController<MareConfigurationBase>
{
    public MareBaseConfigurationController(IOptionsMonitor<MareConfigurationBase> config, ILogger<MareBaseConfigurationController> logger) : base(config, logger)
    {
    }
}

public class MareServerConfigurationController : MareConfigurationController<ServerConfiguration>
{
    public MareServerConfigurationController(IOptionsMonitor<ServerConfiguration> config, ILogger<MareServerConfigurationController> logger) : base(config, logger)
    {
    }
}

public class MareServicesConfigurationController : MareConfigurationController<ServicesConfiguration>
{
    public MareServicesConfigurationController(IOptionsMonitor<ServicesConfiguration> config, ILogger<MareServicesConfigurationController> logger) : base(config, logger)
    {
    }
}
#pragma warning restore MA0048 // File name must match type name
