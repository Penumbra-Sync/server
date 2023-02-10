using Microsoft.AspNetCore.Http;

namespace MareSynchronosShared;

public static class Extensions
{
    private static long _noIpCntr = 0;
    public static string GetIpAddress(this IHttpContextAccessor accessor)
    {
        try
        {
            if (!string.IsNullOrEmpty(accessor.HttpContext.Request.Headers["CF-CONNECTING-IP"]))
                return accessor.HttpContext.Request.Headers["CF-CONNECTING-IP"];

            if (!string.IsNullOrEmpty(accessor.HttpContext.Request.Headers["X-Forwarded-For"]))
            {
                return accessor.HttpContext.Request.Headers["X-Forwarded-For"];
            }

            var ipAddress = accessor.HttpContext.GetServerVariable("HTTP_X_FORWARDED_FOR");

            if (!string.IsNullOrWhiteSpace(ipAddress))
            {
                var addresses = ipAddress.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var lastEntry = addresses.LastOrDefault();
                if (lastEntry != null)
                {
                    return lastEntry;
                }
            }

            return accessor.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "NoIp";
        }
        catch
        {
            return "NoIp" + _noIpCntr++;
        }
    }
}