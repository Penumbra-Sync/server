using Microsoft.EntityFrameworkCore;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;

namespace MareSynchronosServer;

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
            var options = services.GetRequiredService<IConfigurationService<ServerConfiguration>>();
            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("Loaded MareSynchronos Server Configuration (IsMain: {isMain})", options.IsMain);
            logger.LogInformation(options.ToString());

            if (options.IsMain)
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

            metrics.SetGaugeTo(MetricsAPI.GaugeUsersRegistered, context.Users.AsNoTracking().Count());
            metrics.SetGaugeTo(MetricsAPI.GaugePairs, context.ClientPairs.AsNoTracking().Count());
            metrics.SetGaugeTo(MetricsAPI.GaugePairsPaused, context.ClientPairs.AsNoTracking().Count(p => p.IsPaused));

        }

        if (args.Length == 0 || !string.Equals(args[0], "dry", StringComparison.Ordinal))
        {
            try
            {
                host.Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
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
