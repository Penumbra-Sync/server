using Grpc.Net.Client.Configuration;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Protos;
using MareSynchronosShared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
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

        var mareSettings = Configuration.GetRequiredSection("MareSynchronos");

        bool isSecondary = mareSettings.GetValue<bool>("IsSecondaryInstance", false);

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


        if (!isSecondary)
        {
            services.AddSingleton(new MareMetrics(new List<string>
            {
            }, new List<string>
            {
                MetricsAPI.GaugeFilesTotalSize,
                MetricsAPI.GaugeFilesTotal
            }));
            services.AddHostedService<CleanupService>();
        }

        services.AddSingleton<GrpcAuthenticationService>();
        services.AddGrpcClient<AuthService.AuthServiceClient>(c =>
        {
            c.Address = new Uri(mareSettings.GetValue<string>("ServiceAddress"));
        }).ConfigureChannel(c =>
        {
            c.ServiceConfig = new ServiceConfig { MethodConfigs = { defaultMethodConfig } };
        });

        services.AddDbContextPool<MareDbContext>(options =>
        {
            options.UseNpgsql(Configuration.GetConnectionString("DefaultConnection"), builder =>
            {
                builder.MigrationsHistoryTable("_efmigrationshistory", "public");
            }).UseSnakeCaseNamingConvention();
            options.EnableThreadSafetyChecks(false);
        }, mareSettings.GetValue("DbContextPoolSize", 1024));

        services.AddHostedService(p => p.GetService<GrpcAuthenticationService>());

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = SecretKeyGrpcAuthenticationHandler.AuthScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, SecretKeyGrpcAuthenticationHandler>(SecretKeyGrpcAuthenticationHandler.AuthScheme, options => { });
        services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddGrpc(o =>
        {
            o.MaxReceiveMessageSize = null;
        });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        bool isSecondary = Configuration.GetSection("MareSynchronos").GetValue<bool>("IsSecondaryInstance", false);

        app.UseHttpLogging();

        app.UseRouting();

        if (!isSecondary)
        {
            var metricServer = new KestrelMetricServer(4981);
            metricServer.Start();
        }

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseStaticFiles(new StaticFileOptions()
        {
            FileProvider = new PhysicalFileProvider(Configuration.GetRequiredSection("MareSynchronos")["CacheDirectory"]),
            RequestPath = "/cache",
            ServeUnknownFileTypes = true
        });

        if (!isSecondary)
        {
            app.UseEndpoints(e =>
            {
                e.MapGrpcService<FileService>();
            });
        }
    }
}