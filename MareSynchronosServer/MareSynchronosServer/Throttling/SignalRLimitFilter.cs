using AspNetCoreRateLimit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace MareSynchronosServer.Throttling;
public class SignalRLimitFilter : IHubFilter
{
    private readonly IRateLimitProcessor _processor;
    private readonly IHttpContextAccessor accessor;
    private readonly ILogger<SignalRLimitFilter> logger;
    private static SemaphoreSlim ConnectionLimiterSemaphore = new(20);
    private static SemaphoreSlim DisconnectLimiterSemaphore = new(20);

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
                var authUserId = invocationContext.Context.User.Claims?.SingleOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value ?? "Unknown";
                var retry = counter.Timestamp.RetryAfterFrom(rule);
                logger.LogWarning($"Method rate limit triggered from {ip}/{authUserId}: {invocationContext.HubMethodName}");
                throw new HubException($"call limit {retry}");
            }
        }

        return await next(invocationContext);
    }

    // Optional method
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        await ConnectionLimiterSemaphore.WaitAsync();
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
                ConnectionLimiterSemaphore.Release();
                throw new HubException($"Connection rate limit {retry}");
            }
        }

        try
        {
            await Task.Delay(250);
            await next(context);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error on OnConnectedAsync");
        }
        finally
        {
            ConnectionLimiterSemaphore.Release();
        }
    }

    public async Task OnDisconnectedAsync(
        HubLifetimeContext context, Exception exception, Func<HubLifetimeContext, Exception, Task> next)
    {
        await DisconnectLimiterSemaphore.WaitAsync();
        try
        {
            await next(context, exception);
            await Task.Delay(250);
        }
        catch (Exception e)
        {
            logger.LogWarning(exception, "InitialException on OnDisconnectedAsync");
            logger.LogWarning(e, "ThrownException on OnDisconnectedAsync");
        }
        finally
        {
            DisconnectLimiterSemaphore.Release();
        }
    }
}
