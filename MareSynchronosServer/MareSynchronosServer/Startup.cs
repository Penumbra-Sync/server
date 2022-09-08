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

namespace MareSynchronosServer
{
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

            services.AddHostedService(provider => provider.GetService<SystemInfoService>());

            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = SecretKeyGrpcAuthenticationHandler.AuthScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, SecretKeyGrpcAuthenticationHandler>(SecretKeyGrpcAuthenticationHandler.AuthScheme, options => { });
            services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

            services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();

            var signalRserviceBuilder = services.AddSignalR(hubOptions =>
            {
                hubOptions.MaximumReceiveMessageSize = long.MaxValue;
                hubOptions.EnableDetailedErrors = true;
                hubOptions.MaximumParallelInvocationsPerClient = 10;
                hubOptions.StreamBufferCapacity = 200;
                hubOptions.AddFilter<SignalRLimitFilter>();
            });
            var redis = mareConfig.GetValue<string>("RedisConnectionString", string.Empty);
            if (!string.IsNullOrEmpty(redis))
            {
                signalRserviceBuilder.AddStackExchangeRedis(redis, options =>
                {
                    options.Configuration.ChannelPrefix = "MareSynchronos";
                });
            }
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
}
