using MareSynchronosServices;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Linq;

public class Program
{
    public static void Main(string[] args)
    {
        var hostBuilder = CreateHostBuilder(args);
        var host = hostBuilder.Build();

        using (var scope = host.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            using var dbContext = services.GetRequiredService<MareDbContext>();
            var metrics = services.GetRequiredService<MareMetrics>();

            metrics.SetGaugeTo(MetricsAPI.GaugeUsersRegistered, dbContext.Users.Count());
        }

        host.Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseSystemd()
            .UseConsoleLifetime()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseContentRoot(AppContext.BaseDirectory);
                webBuilder.UseStartup<Startup>();
            });
}