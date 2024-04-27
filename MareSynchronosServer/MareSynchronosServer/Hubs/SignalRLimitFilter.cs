using AspNetCoreRateLimit;
using MareSynchronosShared;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace MareSynchronosServer.Hubs;
public class SignalRLimitFilter : IHubFilter
{
    private readonly IRateLimitProcessor _processor;
    private readonly IHttpContextAccessor accessor;
    private readonly ILogger<SignalRLimitFilter> logger;
    private static readonly SemaphoreSlim ConnectionLimiterSemaphore = new(20, 20);
    private static readonly SemaphoreSlim DisconnectLimiterSemaphore = new(20, 20);

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
            ClientId = invocationContext.Context.UserIdentifier,
        };
        foreach (var rule in await _processor.GetMatchingRulesAsync(client).ConfigureAwait(false))
        {
            var counter = await _processor.ProcessRequestAsync(client, rule).ConfigureAwait(false);
            if (counter.Count > rule.Limit)
            {
                var authUserId = invocationContext.Context.User.Claims?.SingleOrDefault(c => string.Equals(c.Type, MareClaimTypes.Uid, StringComparison.Ordinal))?.Value ?? "Unknown";
                var retry = counter.Timestamp.RetryAfterFrom(rule);
                logger.LogWarning("Method rate limit triggered from {ip}/{authUserId}: {method}", ip, authUserId, invocationContext.HubMethodName);
                throw new HubException($"call limit {retry}");
            }
        }

        return await next(invocationContext).ConfigureAwait(false);
    }

    // Optional method
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        await ConnectionLimiterSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var ip = accessor.GetIpAddress();
            var client = new ClientRequestIdentity
            {
                ClientIp = ip,
                Path = "Connect",
                HttpVerb = "ws",
            };
            foreach (var rule in await _processor.GetMatchingRulesAsync(client).ConfigureAwait(false))
            {
                var counter = await _processor.ProcessRequestAsync(client, rule).ConfigureAwait(false);
                if (counter.Count > rule.Limit)
                {
                    var retry = counter.Timestamp.RetryAfterFrom(rule);
                    logger.LogWarning("Connection rate limit triggered from {ip}", ip);
                    ConnectionLimiterSemaphore.Release();
                    throw new HubException($"Connection rate limit {retry}");
                }
            }


            await Task.Delay(25).ConfigureAwait(false);
            await next(context).ConfigureAwait(false);
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
        await DisconnectLimiterSemaphore.WaitAsync().ConfigureAwait(false);
        if (exception != null)
        {
            logger.LogWarning(exception, "InitialException on OnDisconnectedAsync");
        }

        try
        {
            await next(context, exception).ConfigureAwait(false);
            await Task.Delay(25).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "ThrownException on OnDisconnectedAsync");
        }
        finally
        {
            DisconnectLimiterSemaphore.Release();
        }
    }
}
