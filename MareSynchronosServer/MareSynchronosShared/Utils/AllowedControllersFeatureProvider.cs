using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Controllers;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace MareSynchronosShared.Utils;

public class AllowedControllersFeatureProvider : ControllerFeatureProvider
{
    private readonly ILogger _logger;
    private readonly Type[] _allowedTypes;

    public AllowedControllersFeatureProvider(params Type[] allowedTypes)
    {
        _allowedTypes = allowedTypes;
    }

    protected override bool IsController(TypeInfo typeInfo)
    {
        return base.IsController(typeInfo) && _allowedTypes.Contains(typeInfo.AsType());
    }
}
