using MareSynchronosServer;
using MareSynchronosServices.Authentication;
using MareSynchronosServices.Discord;
using MareSynchronosServices.Metrics;
using MareSynchronosServices.Services;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;

namespace MareSynchronosServices;

public class Startup
{
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddDbContextPool<MareDbContext>(options =>
        {
            options.UseNpgsql(Configuration.GetConnectionString("DefaultConnection"), builder =>
            {
                builder.MigrationsHistoryTable("_efmigrationshistory", "public");
            }).UseSnakeCaseNamingConvention();
            options.EnableThreadSafetyChecks(false);
        }, Configuration.GetValue("DbContextPoolSize", 1024));

        services.AddSingleton<MareMetrics>();
        services.AddSingleton<SecretKeyAuthenticationHandler>();
        services.AddSingleton<CleanupService>();
        services.AddTransient(_ => Configuration);
        services.AddHostedService(provider => provider.GetService<CleanupService>());
        services.AddHostedService<DiscordBot>();
        services.AddGrpc();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseRouting();

        var metricServer = new KestrelMetricServer(4980);
        metricServer.Start();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapGrpcService<AuthenticationService>();
            endpoints.MapGrpcService<MetricsService>();
        });
    }
}