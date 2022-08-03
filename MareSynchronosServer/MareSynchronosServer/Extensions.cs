using Microsoft.AspNetCore.Http;

namespace MareSynchronosServer
{
    public static class Extensions
    {
        public static string GetIpAddress(this IHttpContextAccessor accessor)
        {
            if (!string.IsNullOrEmpty(accessor.HttpContext.Request.Headers["CF-CONNECTING-IP"]))
                return accessor.HttpContext.Request.Headers["CF-CONNECTING-IP"];

            var ipAddress = accessor.HttpContext.GetServerVariable("HTTP_X_FORWARDED_FOR");

            if (!string.IsNullOrEmpty(ipAddress))
            {
                var addresses = ipAddress.Split(',');
                if (addresses.Length != 0)
                    return addresses.Last();
            }

            return accessor.HttpContext.Connection.RemoteIpAddress.ToString();
        }
    }
}
