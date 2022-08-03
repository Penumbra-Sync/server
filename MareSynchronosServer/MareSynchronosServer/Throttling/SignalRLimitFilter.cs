using AspNetCoreRateLimit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System;
using System.Threading.Tasks;

namespace MareSynchronosServer.Throttling;
public class SignalRLimitFilter : IHubFilter
{
    private readonly IRateLimitProcessor _processor;
    private readonly IHttpContextAccessor accessor;

    public SignalRLimitFilter(
        IOptions<IpRateLimitOptions> options, IProcessingStrategy processing, IIpPolicyStore policyStore, IHttpContextAccessor accessor)
    {
        _processor = new IpRateLimitProcessor(options?.Value, policyStore, processing);
        this.accessor = accessor;
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
            Console.WriteLine("time: {0}, count: {1}", counter.Timestamp, counter.Count);
            if (counter.Count > rule.Limit)
            {
                var retry = counter.Timestamp.RetryAfterFrom(rule);
                throw new HubException($"call limit {retry}");
            }
        }

        Console.WriteLine($"Calling hub method '{invocationContext.HubMethodName}'");
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
            Console.WriteLine("time: {0}, count: {1}", counter.Timestamp, counter.Count);
            if (counter.Count > rule.Limit)
            {
                var retry = counter.Timestamp.RetryAfterFrom(rule);
                throw new HubException($"rate limit {retry}");
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
