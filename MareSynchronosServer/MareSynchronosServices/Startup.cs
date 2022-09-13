using MareSynchronosServices.Authentication;
using MareSynchronosServices.Discord;
using MareSynchronosServices.Services;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;
using System.Collections.Generic;
using MareSynchronosShared.Services;

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

        services.AddSingleton(new MareMetrics(new List<string> {
            MetricsAPI.CounterAuthenticationRequests,
            MetricsAPI.CounterAuthenticationFailures,
            MetricsAPI.CounterAuthenticationCacheHits,
            MetricsAPI.CounterAuthenticationSuccesses
        }, new List<string> 
        {
            MetricsAPI.GaugeUsersRegistered
        }));

        services.AddSingleton<SecretKeyAuthenticationHandler>();
        services.AddSingleton<CleanupService>();
        services.AddTransient(_ => Configuration);
        services.AddHostedService(provider => provider.GetService<CleanupService>());
        services.AddHostedService<DiscordBot>();
        services.AddGrpc();

        // add redis related options
        var redis = Configuration.GetSection("MareSynchronos").GetValue("RedisConnectionString", string.Empty);
        if (!string.IsNullOrEmpty(redis))
        {
            services.AddStackExchangeRedisCache(opt =>
            {
                opt.Configuration = redis;
                opt.InstanceName = "MareSynchronos";
            });
            services.AddSingleton<IClientIdentificationService, DistributedClientIdentificationService>();
            services.AddHostedService(p => p.GetService<DistributedClientIdentificationService>());
        }
        else
        {
            services.AddSingleton<IClientIdentificationService, LocalClientIdentificationService>();
            services.AddHostedService(p => p.GetService<LocalClientIdentificationService>());
        }
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseRouting();

        var metricServer = new KestrelMetricServer(4982);
        metricServer.Start();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapGrpcService<AuthenticationService>();
        });
    }
}