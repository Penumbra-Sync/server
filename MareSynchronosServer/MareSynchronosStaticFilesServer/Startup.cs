using Grpc.Net.Client.Configuration;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Prometheus;
using System;
using System.Collections.Generic;

namespace MareSynchronosStaticFilesServer;

public class Startup
{
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        services.AddTransient(_ => Configuration);

        services.AddLogging();

        var mareSettings = Configuration.GetRequiredSection("MareSynchronos");

        //services.AddControllers();

        var defaultMethodConfig = new MethodConfig
        {
            Names = { MethodName.Default },
            RetryPolicy = new RetryPolicy
            {
                MaxAttempts = 100,
                InitialBackoff = TimeSpan.FromSeconds(1),
                MaxBackoff = TimeSpan.FromSeconds(5),
                BackoffMultiplier = 1.5,
                RetryableStatusCodes = { Grpc.Core.StatusCode.Unavailable }
            }
        };

        services.AddSingleton<MareMetrics>(m => new MareMetrics(m.GetService<ILogger<MareMetrics>>(), new List<string>
        {
            MetricsAPI.CounterAuthenticationCacheHits,
            MetricsAPI.CounterAuthenticationFailures,
            MetricsAPI.CounterAuthenticationRequests,
            MetricsAPI.CounterAuthenticationSuccesses
        }, new List<string>
        {
            MetricsAPI.GaugeFilesTotalSize,
            MetricsAPI.GaugeFilesTotal
        }));
        services.AddHostedService<CleanupService>();

        services.AddSingleton<SecretKeyAuthenticatorService>();
        services.AddDbContextPool<MareDbContext>(options =>
        {
            options.UseNpgsql(Configuration.GetConnectionString("DefaultConnection"), builder =>
            {
                builder.MigrationsHistoryTable("_efmigrationshistory", "public");
            }).UseSnakeCaseNamingConvention();
            options.EnableThreadSafetyChecks(false);
        }, mareSettings.GetValue("DbContextPoolSize", 1024));

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = SecretKeyAuthenticationHandler.AuthScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, SecretKeyAuthenticationHandler>(SecretKeyAuthenticationHandler.AuthScheme, options => { });
        services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddGrpc(o =>
        {
            o.MaxReceiveMessageSize = null;
        });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseHttpLogging();

        app.UseRouting();

        var metricServer = new KestrelMetricServer(4981);
        metricServer.Start();

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseStaticFiles(new StaticFileOptions()
        {
            FileProvider = new PhysicalFileProvider(Configuration.GetRequiredSection("MareSynchronos")["CacheDirectory"]),
            RequestPath = "/cache",
            ServeUnknownFileTypes = true,

        });

        app.UseEndpoints(e =>
        {
            e.MapGrpcService<FileService>();
            //e.MapControllers();
        });
    }
}