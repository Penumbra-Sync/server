using AspNetCoreRateLimit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Threading.Tasks;

namespace MareSynchronosServer.Throttling;
public class SignalRLimitFilter : IHubFilter
{
    private readonly IRateLimitProcessor _processor;
    private readonly IHttpContextAccessor accessor;
    private readonly ILogger<SignalRLimitFilter> logger;

    public SignalRLimitFilter(
        IOptions<IpRateLimitOptions> options, IProcessingStrategy processing, IIpPolicyStore policyStore, IHttpContextAccessor accessor, ILogger<SignalRLimitFilter> logger)
    {
        _processor = new IpRateLimitProcessor(options?.Value, policyStore, processing);
        this.accessor = accessor;
        this.logger = logger;
    }

    public async ValueTask<object> InvokeMethodAsync(
        HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object>> next)
    {
        var ip = accessor.GetIpAddress();
        var client = new ClientRequestIdentity
        {
            ClientIp = ip,
            Path = invocationContext.HubMethodName,
            HttpVerb = "ws",
            ClientId = invocationContext.Context.UserIdentifier
        };
        foreach (var rule in await _processor.GetMatchingRulesAsync(client))
        {
            var counter = await _processor.ProcessRequestAsync(client, rule);
            if (counter.Count > rule.Limit)
            {
                var retry = counter.Timestamp.RetryAfterFrom(rule);
                logger.LogWarning($"Method rate limit triggered from {ip}: {invocationContext.HubMethodName}");
                throw new HubException($"call limit {retry}");
            }
        }

        return await next(invocationContext);
    }

    // Optional method
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var ip = accessor.GetIpAddress();
        var client = new ClientRequestIdentity
        {
            ClientIp = ip,
            Path = "Connect",
            HttpVerb = "ws",
        };
        foreach (var rule in await _processor.GetMatchingRulesAsync(client))
        {
            var counter = await _processor.ProcessRequestAsync(client, rule);
            if (counter.Count > rule.Limit)
            {
                var retry = counter.Timestamp.RetryAfterFrom(rule);
                logger.LogWarning($"Connection rate limit triggered from {ip}");
                throw new HubException($"Connection rate limit {retry}");
            }
        }

        await next(context);
    }

    // Optional method
    public Task OnDisconnectedAsync(
        HubLifetimeContext context, Exception exception, Func<HubLifetimeContext, Exception, Task> next)
    {
        return next(context, exception);
    }
}
