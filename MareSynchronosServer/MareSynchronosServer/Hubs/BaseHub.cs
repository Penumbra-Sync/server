using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MareSynchronosServer.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Hubs
{
    public abstract class BaseHub<T> : Hub
    {
        protected readonly ILogger<T> Logger;
        protected MareDbContext DbContext { get; init; }

        protected BaseHub(MareDbContext context, ILogger<T> logger)
        {
            Logger = logger;
            DbContext = context;
        }

        protected string AuthenticatedUserId => Context.User?.Claims?.SingleOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value ?? "Unknown";

        protected async Task<Models.User> GetAuthenticatedUserUntrackedAsync()
        {
            return await DbContext.Users.AsNoTrackingWithIdentityResolution().SingleAsync(u => u.UID == AuthenticatedUserId);
        }
        
        public static string GenerateRandomString(int length, string allowableChars = null)
        {
            if (string.IsNullOrEmpty(allowableChars))
                allowableChars = @"ABCDEFGHJKLMNPQRSTUVWXYZ0123456789";

            // Generate random data
            var rnd = new byte[length];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(rnd);

            // Generate the output string
            var allowable = allowableChars.ToCharArray();
            var l = allowable.Length;
            var chars = new char[length];
            for (var i = 0; i < length; i++)
                chars[i] = allowable[rnd[i] % l];

            return new string(chars);
        }

    }
}