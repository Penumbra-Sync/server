using Grpc.Core;
using MareSynchronosShared.Protos;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MareSynchronosShared.Services;

[Authorize]
[AllowAnonymous]
public class GrpcConfigurationService<T> : ConfigurationService.ConfigurationServiceBase where T : class, IMareConfiguration
{
    private readonly T _config;
    private readonly ILogger<GrpcConfigurationService<T>> logger;

    public GrpcConfigurationService(IOptions<T> config, ILogger<GrpcConfigurationService<T>> logger)
    {
        _config = config.Value;
        this.logger = logger;
    }

    [AllowAnonymous]
    public override Task<ValueMessage> GetConfigurationEntry(KeyMessage request, ServerCallContext context)
    {
        logger.LogInformation("Remote requested {key}", request.Key);
        var returnVal = _config.SerializeValue(request.Key, request.Default);
        return Task.FromResult(new ValueMessage() { Value = returnVal });
    }
}