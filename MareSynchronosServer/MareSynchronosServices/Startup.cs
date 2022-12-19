using MareSynchronosServices.Discord;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;
using System.Collections.Generic;
using MareSynchronosServices.Identity;
using Microsoft.Extensions.Logging;

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

        services.AddSingleton(m => new MareMetrics(m.GetService<ILogger<MareMetrics>>(), new List<string> {
        }, new List<string> 
        {
            MetricsAPI.GaugeUsersRegistered
        }));

        services.AddTransient(_ => Configuration);
        services.AddSingleton<DiscordBotServices>();
        services.AddSingleton<IdentityHandler>();
        services.AddSingleton<CleanupService>();
        services.AddHostedService(provider => provider.GetService<CleanupService>());
        services.AddHostedService<DiscordBot>();
        services.AddGrpc();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseRouting();

        var metricServer = new KestrelMetricServer(4982);
        metricServer.Start();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapGrpcService<IdentityService>();
        });
    }
}