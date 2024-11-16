using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using MareSynchronosStaticFilesServer.Controllers;
using MareSynchronosStaticFilesServer.Services;
using MareSynchronosStaticFilesServer.Utils;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Prometheus;
using StackExchange.Redis.Extensions.Core.Configuration;
using StackExchange.Redis.Extensions.System.Text.Json;
using StackExchange.Redis;
using System.Net;
using System.Text;
using MareSynchronosShared.Utils.Configuration;

namespace MareSynchronosStaticFilesServer;

public class Startup
{
    private bool _isMain;
    private bool _isDistributionNode;
    private readonly ILogger<Startup> _logger;

    public Startup(IConfiguration configuration, ILogger<Startup> logger)
    {
        Configuration = configuration;
        _logger = logger;
        var mareSettings = Configuration.GetRequiredSection("MareSynchronos");
        _isDistributionNode = mareSettings.GetValue(nameof(StaticFilesServerConfiguration.IsDistributionNode), false);
        _isMain = string.IsNullOrEmpty(mareSettings.GetValue(nameof(StaticFilesServerConfiguration.MainFileServerAddress), string.Empty)) && _isDistributionNode;
    }

    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        services.AddLogging();

        services.Configure<StaticFilesServerConfiguration>(Configuration.GetRequiredSection("MareSynchronos"));
        services.Configure<MareConfigurationBase>(Configuration.GetRequiredSection("MareSynchronos"));
        services.Configure<KestrelServerOptions>(Configuration.GetSection("Kestrel"));
        services.AddSingleton(Configuration);

        var mareConfig = Configuration.GetRequiredSection("MareSynchronos");

        // metrics configuration
        services.AddSingleton(m => new MareMetrics(m.GetService<ILogger<MareMetrics>>(), new List<string>
        {
            MetricsAPI.CounterFileRequests,
            MetricsAPI.CounterFileRequestSize
        }, new List<string>
        {
            MetricsAPI.GaugeFilesTotalColdStorage,
            MetricsAPI.GaugeFilesTotalSizeColdStorage,
            MetricsAPI.GaugeFilesTotalSize,
            MetricsAPI.GaugeFilesTotal,
            MetricsAPI.GaugeFilesUniquePastDay,
            MetricsAPI.GaugeFilesUniquePastDaySize,
            MetricsAPI.GaugeFilesUniquePastHour,
            MetricsAPI.GaugeFilesUniquePastHourSize,
            MetricsAPI.GaugeCurrentDownloads,
            MetricsAPI.GaugeDownloadQueue,
            MetricsAPI.GaugeDownloadQueueCancelled,
            MetricsAPI.GaugeDownloadPriorityQueue,
            MetricsAPI.GaugeDownloadPriorityQueueCancelled,
            MetricsAPI.GaugeQueueFree,
            MetricsAPI.GaugeQueueInactive,
            MetricsAPI.GaugeQueueActive,
            MetricsAPI.GaugeFilesDownloadingFromCache,
            MetricsAPI.GaugeFilesTasksWaitingForDownloadFromCache
        }));

        // generic services
        services.AddSingleton<CachedFileProvider>();
        services.AddSingleton<FileStatisticsService>();
        services.AddSingleton<RequestFileStreamResultFactory>();
        services.AddSingleton<ServerTokenGenerator>();
        services.AddSingleton<RequestQueueService>();
        services.AddHostedService(p => p.GetService<RequestQueueService>());
        services.AddHostedService(m => m.GetService<FileStatisticsService>());
        services.AddSingleton<IConfigurationService<MareConfigurationBase>, MareConfigurationServiceClient<MareConfigurationBase>>();
        services.AddHostedService(p => (MareConfigurationServiceClient<MareConfigurationBase>)p.GetService<IConfigurationService<MareConfigurationBase>>());

        // specific services
        if (_isMain)
        {
            services.AddSingleton<IClientReadyMessageService, MainClientReadyMessageService>();
            services.AddHostedService<MainFileCleanupService>();
            services.AddSingleton<IConfigurationService<StaticFilesServerConfiguration>, MareConfigurationServiceServer<StaticFilesServerConfiguration>>();
            services.AddSingleton<MainServerShardRegistrationService>();
            services.AddHostedService(s => s.GetRequiredService<MainServerShardRegistrationService>());
            services.AddDbContextPool<MareDbContext>(options =>
            {
                options.UseNpgsql(Configuration.GetConnectionString("DefaultConnection"), builder =>
                {
                    builder.MigrationsHistoryTable("_efmigrationshistory", "public");
                }).UseSnakeCaseNamingConvention();
                options.EnableThreadSafetyChecks(false);
            }, mareConfig.GetValue(nameof(MareConfigurationBase.DbContextPoolSize), 1024));
            services.AddDbContextFactory<MareDbContext>(options =>
            {
                options.UseNpgsql(Configuration.GetConnectionString("DefaultConnection"), builder =>
                {
                    builder.MigrationsHistoryTable("_efmigrationshistory", "public");
                    builder.MigrationsAssembly("MareSynchronosShared");
                }).UseSnakeCaseNamingConvention();
                options.EnableThreadSafetyChecks(false);
            });

            var signalRServiceBuilder = services.AddSignalR(hubOptions =>
            {
                hubOptions.MaximumReceiveMessageSize = long.MaxValue;
                hubOptions.EnableDetailedErrors = true;
                hubOptions.MaximumParallelInvocationsPerClient = 10;
                hubOptions.StreamBufferCapacity = 200;
            }).AddMessagePackProtocol(opt =>
            {
                var resolver = CompositeResolver.Create(StandardResolverAllowPrivate.Instance,
                    BuiltinResolver.Instance,
                    AttributeFormatterResolver.Instance,
                    // replace enum resolver
                    DynamicEnumAsStringResolver.Instance,
                    DynamicGenericResolver.Instance,
                    DynamicUnionResolver.Instance,
                    DynamicObjectResolver.Instance,
                    PrimitiveObjectResolver.Instance,
                    // final fallback(last priority)
                    StandardResolver.Instance);

                opt.SerializerOptions = MessagePackSerializerOptions.Standard
                    .WithCompression(MessagePackCompression.Lz4Block)
                    .WithResolver(resolver);
            });

            // configure redis for SignalR
            var redisConnection = mareConfig.GetValue(nameof(ServerConfiguration.RedisConnectionString), string.Empty);
            signalRServiceBuilder.AddStackExchangeRedis(redisConnection, options => { });

            var options = ConfigurationOptions.Parse(redisConnection);

            var endpoint = options.EndPoints[0];
            string address = "";
            int port = 0;
            if (endpoint is DnsEndPoint dnsEndPoint) { address = dnsEndPoint.Host; port = dnsEndPoint.Port; }
            if (endpoint is IPEndPoint ipEndPoint) { address = ipEndPoint.Address.ToString(); port = ipEndPoint.Port; }
            var redisConfiguration = new RedisConfiguration()
            {
                AbortOnConnectFail = true,
                KeyPrefix = "",
                Hosts = new RedisHost[]
                {
                new RedisHost(){ Host = address, Port = port },
                },
                AllowAdmin = true,
                ConnectTimeout = options.ConnectTimeout,
                Database = 0,
                Ssl = false,
                Password = options.Password,
                ServerEnumerationStrategy = new ServerEnumerationStrategy()
                {
                    Mode = ServerEnumerationStrategy.ModeOptions.All,
                    TargetRole = ServerEnumerationStrategy.TargetRoleOptions.Any,
                    UnreachableServerAction = ServerEnumerationStrategy.UnreachableServerActionOptions.Throw,
                },
                MaxValueLength = 1024,
                PoolSize = mareConfig.GetValue(nameof(ServerConfiguration.RedisPool), 50),
                SyncTimeout = options.SyncTimeout,
            };

            services.AddStackExchangeRedisExtensions<SystemTextJsonSerializer>(redisConfiguration);
        }
        else
        {
            services.AddSingleton<ShardRegistrationService>();
            services.AddHostedService(s => s.GetRequiredService<ShardRegistrationService>());
            services.AddSingleton<IClientReadyMessageService, ShardClientReadyMessageService>();
            services.AddHostedService<ShardFileCleanupService>();
            services.AddSingleton<IConfigurationService<StaticFilesServerConfiguration>, MareConfigurationServiceClient<StaticFilesServerConfiguration>>();
            services.AddHostedService(p => (MareConfigurationServiceClient<StaticFilesServerConfiguration>)p.GetService<IConfigurationService<StaticFilesServerConfiguration>>());
        }

        services.AddMemoryCache();

        // controller setup
        services.AddControllers().ConfigureApplicationPartManager(a =>
        {
            a.FeatureProviders.Remove(a.FeatureProviders.OfType<ControllerFeatureProvider>().First());
            if (_isMain)
            {
                a.FeatureProviders.Add(new AllowedControllersFeatureProvider(typeof(MareStaticFilesServerConfigurationController),
                    typeof(CacheController), typeof(RequestController), typeof(ServerFilesController),
                    typeof(DistributionController), typeof(MainController), typeof(SpeedTestController)));
            }
            else if (_isDistributionNode)
            {
                a.FeatureProviders.Add(new AllowedControllersFeatureProvider(typeof(CacheController), typeof(RequestController), typeof(DistributionController), typeof(SpeedTestController)));
            }
            else
            {
                a.FeatureProviders.Add(new AllowedControllersFeatureProvider(typeof(CacheController), typeof(RequestController), typeof(SpeedTestController)));
            }
        });

        // authentication and authorization 
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IConfigurationService<MareConfigurationBase>>((o, s) =>
            {
                o.TokenValidationParameters = new()
                {
                    ValidateIssuer = false,
                    ValidateLifetime = true,
                    ValidateAudience = false,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(s.GetValue<string>(nameof(MareConfigurationBase.Jwt))))
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
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            options.AddPolicy("Internal", new AuthorizationPolicyBuilder().RequireClaim(MareClaimTypes.Internal, "true").Build());
        });
        services.AddSingleton<IUserIdProvider, IdBasedUserIdProvider>();

        services.AddHealthChecks();
        services.AddHttpLogging(e => e = new Microsoft.AspNetCore.HttpLogging.HttpLoggingOptions());
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseHttpLogging();

        app.UseRouting();

        var config = app.ApplicationServices.GetRequiredService<IConfigurationService<MareConfigurationBase>>();

        var metricServer = new KestrelMetricServer(config.GetValueOrDefault<int>(nameof(MareConfigurationBase.MetricsPort), 4981));
        metricServer.Start();

        app.UseHttpMetrics();

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseEndpoints(e =>
        {
            if (_isMain)
            {
                e.MapHub<MareSynchronosServer.Hubs.MareHub>("/dummyhub");
            }

            e.MapControllers();
            e.MapHealthChecks("/health").WithMetadata(new AllowAnonymousAttribute());
        });
    }
}