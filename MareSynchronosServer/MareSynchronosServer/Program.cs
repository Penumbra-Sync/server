using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;

namespace MareSynchronosServer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var hostBuilder = CreateHostBuilder(args);
            var host = hostBuilder.Build();
            using (var scope = host.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                using var context = services.GetRequiredService<MareDbContext>();

                var secondaryServer = Environment.GetEnvironmentVariable("SECONDARY_SERVER");
                if (string.IsNullOrEmpty(secondaryServer) || secondaryServer == "0")
                {
                    context.Database.Migrate();
                    context.SaveChanges();

                    // clean up residuals
                    var looseFiles = context.Files.Where(f => f.Uploaded == false);
                    var unfinishedRegistrations = context.LodeStoneAuth.Where(c => c.StartedAt != null);
                    context.RemoveRange(unfinishedRegistrations);
                    context.RemoveRange(looseFiles);
                    context.SaveChanges();
                }

                var metrics = services.GetRequiredService<MareMetrics>();

                metrics.SetGaugeTo(MetricsAPI.GaugePairs, context.ClientPairs.Count());
                metrics.SetGaugeTo(MetricsAPI.GaugePairsPaused, context.ClientPairs.Count(p => p.IsPaused));
            }

            if (args.Length == 0 || args[0] != "dry")
            {
                host.Run();
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseSystemd()
                .UseConsoleLifetime()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseContentRoot(AppContext.BaseDirectory);
                    webBuilder.ConfigureLogging((ctx, builder) =>
                    {
                        builder.AddConfiguration(ctx.Configuration.GetSection("Logging"));
                        builder.AddFile(o => o.RootPath = AppContext.BaseDirectory);
                    });
                    webBuilder.UseStartup<Startup>();
                });
    }
}
