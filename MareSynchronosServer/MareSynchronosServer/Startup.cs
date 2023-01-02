using MareSynchronos.API;
using Microsoft.EntityFrameworkCore;
using MareSynchronosServer.Hubs;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using AspNetCoreRateLimit;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Data;
using MareSynchronosShared.Protos;
using Grpc.Net.Client.Configuration;
using MareSynchronosShared.Metrics;
using MareSynchronosServer.Services;
using MareSynchronosServer.Utils;
using MareSynchronosServer.RequirementHandlers;
using MareSynchronosShared.Utils;
using MareSynchronosServer.Identity;
using MareSynchronosShared.Services;
using Prometheus;
using Microsoft.Extensions.Options;
using Grpc.Net.ClientFactory;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

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

        services.AddTransient(_ => Configuration);

        var mareConfig = Configuration.GetRequiredSection("MareSynchronos");

        // configure metrics
        ConfigureMetrics(services);

        // configure file service grpc connection
        ConfigureFileServiceGrpcClient(services, mareConfig);

        // configure database
        ConfigureDatabase(services, mareConfig);

        // configure authentication and authorization
        ConfigureAuthorization(services);

        // configure rate limiting
        ConfigureIpRateLimiting(services);

        // configure SignalR
        ConfigureSignalR(services, mareConfig);

        // configure mare specific services
        ConfigureMareServices(services, mareConfig);

        services.AddHealthChecks();
        services.AddControllers();
    }

    private static void ConfigureMareServices(IServiceCollection services, IConfigurationSection mareConfig)
    {
        bool isMainServer = mareConfig.GetValue<Uri>(nameof(ServerConfiguration.MainServerGrpcAddress), defaultValue: null) == null;

        services.Configure<ServerConfiguration>(mareConfig);
        services.Configure<MareConfigurationBase>(mareConfig);
        services.Configure<MareConfigurationAuthBase>(mareConfig);

        services.AddSingleton<SystemInfoService>();
        services.AddSingleton<IUserIdProvider, IdBasedUserIdProvider>();
        services.AddHostedService(provider => provider.GetService<SystemInfoService>());
        // configure services based on main server status
        ConfigureIdentityServices(services, mareConfig, isMainServer);

        if (isMainServer)
        {
            services.AddSingleton<UserCleanupService>();
            services.AddHostedService(provider => provider.GetService<UserCleanupService>());
        }
    }

    private static void ConfigureSignalR(IServiceCollection services, IConfigurationSection mareConfig)
    {
        var signalRServiceBuilder = services.AddSignalR(hubOptions =>
        {
            hubOptions.MaximumReceiveMessageSize = long.MaxValue;
            hubOptions.EnableDetailedErrors = true;
            hubOptions.MaximumParallelInvocationsPerClient = 10;
            hubOptions.StreamBufferCapacity = 200;

            hubOptions.AddFilter<SignalRLimitFilter>();
        });

        // configure redis for SignalR
        var redis = mareConfig.GetValue(nameof(ServerConfiguration.RedisConnectionString), string.Empty);
        if (!string.IsNullOrEmpty(redis))
        {
            signalRServiceBuilder.AddStackExchangeRedis(redis, options =>
            {
                options.Configuration.ChannelPrefix = "MareSynchronos";
            });
        }
    }

    private void ConfigureIpRateLimiting(IServiceCollection services)
    {
        services.Configure<IpRateLimitOptions>(Configuration.GetSection("IpRateLimiting"));
        services.Configure<IpRateLimitPolicies>(Configuration.GetSection("IpRateLimitPolicies"));
        services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();
        services.AddMemoryCache();
        services.AddInMemoryRateLimiting();
    }

    private static void ConfigureAuthorization(IServiceCollection services)
    {
        services.AddSingleton<SecretKeyAuthenticatorService>();
        services.AddTransient<IAuthorizationHandler, UserRequirementHandler>();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IConfigurationService<MareConfigurationAuthBase>>((o, s) =>
            {
                o.TokenValidationParameters = new()
                {
                    ValidateIssuer = false,
                    ValidateLifetime = false,
                    ValidateAudience = false,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(s.GetValue<string>(nameof(MareConfigurationAuthBase.Jwt))))
                };
            });

        services.AddAuthentication(o =>
        {
            o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            o.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
        }).AddJwtBearer();

        services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().Build();
            options.AddPolicy("Authenticated", policy =>
            {
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
            });
            options.AddPolicy("Identified", policy =>
            {
                policy.AddRequirements(new UserRequirement(UserRequirements.Identified));
            });
            options.AddPolicy("Admin", policy =>
            {
                policy.AddRequirements(new UserRequirement(UserRequirements.Identified | UserRequirements.Administrator));
            });
            options.AddPolicy("Moderator", policy =>
            {
                policy.AddRequirements(new UserRequirement(UserRequirements.Identified | UserRequirements.Moderator | UserRequirements.Administrator));
            });
        });
    }

    private void ConfigureDatabase(IServiceCollection services, IConfigurationSection mareConfig)
    {
        services.AddDbContextPool<MareDbContext>(options =>
        {
            options.UseNpgsql(Configuration.GetConnectionString("DefaultConnection"), builder =>
            {
                builder.MigrationsHistoryTable("_efmigrationshistory", "public");
                builder.MigrationsAssembly("MareSynchronosShared");
            }).UseSnakeCaseNamingConvention();
            options.EnableThreadSafetyChecks(false);
        }, mareConfig.GetValue(nameof(MareConfigurationBase.DbContextPoolSize), 1024));
    }

    private static void ConfigureMetrics(IServiceCollection services)
    {
        services.AddSingleton<MareMetrics>(m => new MareMetrics(m.GetService<ILogger<MareMetrics>>(), new List<string>
        {
            MetricsAPI.CounterInitializedConnections,
            MetricsAPI.CounterUserPushData,
            MetricsAPI.CounterUserPushDataTo,
            MetricsAPI.CounterUsersRegisteredDeleted,
            MetricsAPI.CounterAuthenticationCacheHits,
            MetricsAPI.CounterAuthenticationFailures,
            MetricsAPI.CounterAuthenticationRequests,
            MetricsAPI.CounterAuthenticationSuccesses,
        }, new List<string>
        {
            MetricsAPI.GaugeAuthorizedConnections,
            MetricsAPI.GaugeConnections,
            MetricsAPI.GaugePairs,
            MetricsAPI.GaugePairsPaused,
            MetricsAPI.GaugeAvailableIOWorkerThreads,
            MetricsAPI.GaugeAvailableWorkerThreads,
            MetricsAPI.GaugeGroups,
            MetricsAPI.GaugeGroupPairs,
            MetricsAPI.GaugeGroupPairsPaused,
            MetricsAPI.GaugeUsersRegistered
        }));
    }

    private static void ConfigureIdentityServices(IServiceCollection services, IConfigurationSection mareConfig, bool isMainServer)
    {
        if (!isMainServer)
        {
            var noRetryConfig = new MethodConfig
            {
                Names = { MethodName.Default },
                RetryPolicy = null
            };

            services.AddGrpcClient<IdentificationService.IdentificationServiceClient>(c =>
            {
                c.Address = new Uri(mareConfig.GetValue<string>(nameof(ServerConfiguration.MainServerGrpcAddress)));
            }).ConfigureChannel(c =>
            {
                c.ServiceConfig = new ServiceConfig { MethodConfigs = { noRetryConfig } };
                c.HttpHandler = new SocketsHttpHandler()
                {
                    EnableMultipleHttp2Connections = true
                };
            });

            services.AddGrpcClient<ConfigurationService.ConfigurationServiceClient>("MainServer", c =>
            {
                c.Address = new Uri(mareConfig.GetValue<string>(nameof(ServerConfiguration.MainServerGrpcAddress)));
            }).ConfigureChannel(c =>
            {
                c.ServiceConfig = new ServiceConfig { MethodConfigs = { noRetryConfig } };
                c.HttpHandler = new SocketsHttpHandler()
                {
                    EnableMultipleHttp2Connections = true
                };
            });

            services.AddSingleton<IClientIdentificationService, GrpcClientIdentificationService>();
            services.AddHostedService(p => p.GetService<IClientIdentificationService>());
            services.AddSingleton<IConfigurationService<ServerConfiguration>>(c => new MareConfigurationServiceClient<ServerConfiguration>(
                c.GetService<ILogger<MareConfigurationServiceClient<ServerConfiguration>>>(),
                c.GetService<IOptions<ServerConfiguration>>(),
                c.GetService<GrpcClientFactory>(),
                "MainServer"));
            services.AddSingleton<IConfigurationService<MareConfigurationAuthBase>>(c => new MareConfigurationServiceClient<MareConfigurationAuthBase>(
                c.GetService<ILogger<MareConfigurationServiceClient<MareConfigurationAuthBase>>>(),
                c.GetService<IOptions<MareConfigurationAuthBase>>(),
                c.GetService<GrpcClientFactory>(),
                "MainServer"));
            services.AddHostedService(p => (MareConfigurationServiceClient<ServerConfiguration>)p.GetService<IConfigurationService<ServerConfiguration>>());
            services.AddHostedService(p => (MareConfigurationServiceClient<MareConfigurationAuthBase>)p.GetService<IConfigurationService<MareConfigurationAuthBase>>());
        }
        else
        {
            services.AddSingleton<IdentityHandler>();
            services.AddSingleton<IClientIdentificationService, LocalClientIdentificationService>();
            services.AddSingleton<IConfigurationService<ServerConfiguration>, MareConfigurationServiceServer<ServerConfiguration>>();
            services.AddSingleton<IConfigurationService<MareConfigurationAuthBase>, MareConfigurationServiceServer<MareConfigurationAuthBase>>();

            services.AddGrpc();
        }
    }

    private static void ConfigureFileServiceGrpcClient(IServiceCollection services, IConfigurationSection mareConfig)
    {
        var defaultMethodConfig = new MethodConfig
        {
            Names = { MethodName.Default },
            RetryPolicy = new RetryPolicy
            {
                MaxAttempts = 1000,
                InitialBackoff = TimeSpan.FromSeconds(1),
                MaxBackoff = TimeSpan.FromSeconds(5),
                BackoffMultiplier = 1.5,
                RetryableStatusCodes = { Grpc.Core.StatusCode.Unavailable }
            }
        };
        services.AddGrpcClient<FileService.FileServiceClient>((serviceProvider, c) =>
        {
            c.Address = serviceProvider.GetRequiredService<IConfigurationService<ServerConfiguration>>()
                .GetValue<Uri>(nameof(ServerConfiguration.StaticFileServiceAddress));
        }).ConfigureChannel(c =>
        {
            c.ServiceConfig = new ServiceConfig { MethodConfigs = { defaultMethodConfig } };
        });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
    {
        logger.LogInformation("Running Configure");

        var config = app.ApplicationServices.GetRequiredService<IConfigurationService<MareConfigurationAuthBase>>();

        app.UseIpRateLimiting();

        app.UseRouting();

        app.UseWebSockets();

        var metricServer = new KestrelMetricServer(config.GetValueOrDefault<int>(nameof(MareConfigurationBase.MetricsPort), 4980));
        metricServer.Start();

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapHub<MareHub>(IMareHub.Path, options =>
            {
                options.ApplicationMaxBufferSize = 5242880;
                options.TransportMaxBufferSize = 5242880;
                options.Transports = HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling;
            });

            if (config.IsMain)
            {
                endpoints.MapGrpcService<GrpcIdentityService>().AllowAnonymous();
                endpoints.MapGrpcService<GrpcConfigurationService<ServerConfiguration>>().AllowAnonymous();
            }

            endpoints.MapHealthChecks("/health").AllowAnonymous();
            endpoints.MapControllers().AllowAnonymous();
        });
    }
}
