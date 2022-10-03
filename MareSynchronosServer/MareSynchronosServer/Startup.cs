using System;
using MareSynchronos.API;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MareSynchronosServer.Hubs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using AspNetCoreRateLimit;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Data;
using MareSynchronosShared.Protos;
using Grpc.Net.Client.Configuration;
using Prometheus;
using MareSynchronosShared.Metrics;
using System.Collections.Generic;
using MareSynchronosServer.Services;
using MareSynchronosShared.Services;
using System.Net.Http;
using MareSynchronosServer.Utils;

namespace MareSynchronosServer;

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

        services.Configure<IpRateLimitOptions>(Configuration.GetSection("IpRateLimiting"));
        services.Configure<IpRateLimitPolicies>(Configuration.GetSection("IpRateLimitPolicies"));

        services.AddMemoryCache();
        services.AddInMemoryRateLimiting();

        services.AddSingleton<SystemInfoService, SystemInfoService>();
        services.AddSingleton<IUserIdProvider, IdBasedUserIdProvider>();
        services.AddTransient(_ => Configuration);

        var mareConfig = Configuration.GetRequiredSection("MareSynchronos");

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

        services.AddSingleton(new MareMetrics(new List<string>
        {
            MetricsAPI.CounterInitializedConnections,
            MetricsAPI.CounterUserPushData,
            MetricsAPI.CounterUserPushDataTo,
            MetricsAPI.CounterUsersRegisteredDeleted,
        }, new List<string>
        {
            MetricsAPI.GaugeAuthorizedConnections,
            MetricsAPI.GaugeConnections,
            MetricsAPI.GaugePairs,
            MetricsAPI.GaugePairsPaused,
            MetricsAPI.GaugeAvailableIOWorkerThreads,
            MetricsAPI.GaugeAvailableWorkerThreads
        }));

        services.AddGrpcClient<AuthService.AuthServiceClient>(c =>
        {
            c.Address = new Uri(mareConfig.GetValue<string>("ServiceAddress"));
        }).ConfigureChannel(c =>
        {
            c.ServiceConfig = new ServiceConfig { MethodConfigs = { defaultMethodConfig } };
            c.HttpHandler = new SocketsHttpHandler()
            {
                EnableMultipleHttp2Connections = true
            };
        });
        services.AddGrpcClient<FileService.FileServiceClient>(c =>
        {
            c.Address = new Uri(mareConfig.GetValue<string>("StaticFileServiceAddress"));
        }).ConfigureChannel(c =>
        {
            c.ServiceConfig = new ServiceConfig { MethodConfigs = { defaultMethodConfig } };
        });

        services.AddDbContextPool<MareDbContext>(options =>
        {
            options.UseNpgsql(Configuration.GetConnectionString("DefaultConnection"), builder =>
            {
                builder.MigrationsHistoryTable("_efmigrationshistory", "public");
                builder.MigrationsAssembly("MareSynchronosShared");
            }).UseSnakeCaseNamingConvention();
            options.EnableThreadSafetyChecks(false);
        }, mareConfig.GetValue("DbContextPoolSize", 1024));

        services.AddAuthentication(options =>
        {
            options.DefaultScheme = SecretKeyGrpcAuthenticationHandler.AuthScheme;
        }).AddScheme<AuthenticationSchemeOptions, SecretKeyGrpcAuthenticationHandler>(SecretKeyGrpcAuthenticationHandler.AuthScheme, _ => { });
        services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();

        var signalRServiceBuilder = services.AddSignalR(hubOptions =>
        {
            hubOptions.MaximumReceiveMessageSize = long.MaxValue;
            hubOptions.EnableDetailedErrors = true;
            hubOptions.MaximumParallelInvocationsPerClient = 10;
            hubOptions.StreamBufferCapacity = 200;

            hubOptions.AddFilter<SignalRLimitFilter>();
        });

        // add redis related options
        var redis = mareConfig.GetValue("RedisConnectionString", string.Empty);
        if (!string.IsNullOrEmpty(redis))
        {
            signalRServiceBuilder.AddStackExchangeRedis(redis, options =>
            {
                options.Configuration.ChannelPrefix = "MareSynchronos";
            });

            services.AddStackExchangeRedisCache(opt =>
            {
                opt.Configuration = redis;
                opt.InstanceName = "MareSynchronosCache:";
            });
            services.AddSingleton<IClientIdentificationService, DistributedClientIdentificationService>();
            services.AddHostedService(p => p.GetService<IClientIdentificationService>());
        }
        else
        {
            services.AddSingleton<IClientIdentificationService, LocalClientIdentificationService>();
            services.AddHostedService(p => p.GetService<IClientIdentificationService>());
        }

        services.AddHostedService(provider => provider.GetService<SystemInfoService>());
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
            app.UseMigrationsEndPoint();
        }
        else
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseIpRateLimiting();

        app.UseRouting();

        app.UseWebSockets();

        var metricServer = new KestrelMetricServer(4980);
        metricServer.Start();

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapHub<MareHub>(Api.Path, options =>
            {
                options.ApplicationMaxBufferSize = 5242880;
                options.TransportMaxBufferSize = 5242880;
                options.Transports = HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling;
            });
        });
    }
}
