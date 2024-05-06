using MareSynchronosServices.Discord;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Services;
using StackExchange.Redis;
using MessagePack.Resolvers;
using MessagePack;
using MareSynchronosShared.Utils.Configuration;

namespace MareSynchronosServices;

public class Startup
{
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        var config = app.ApplicationServices.GetRequiredService<IConfigurationService<MareConfigurationBase>>();

        var metricServer = new KestrelMetricServer(config.GetValueOrDefault<int>(nameof(MareConfigurationBase.MetricsPort), 4982));
        metricServer.Start();

        app.UseRouting();
        app.UseEndpoints(e =>
        {
            e.MapHub<MareSynchronosServer.Hubs.MareHub>("/dummyhub");
        });
    }

    public void ConfigureServices(IServiceCollection services)
    {
        var mareConfig = Configuration.GetSection("MareSynchronos");

        services.AddDbContextPool<MareDbContext>(options =>
        {
            options.UseNpgsql(Configuration.GetConnectionString("DefaultConnection"), builder =>
            {
                builder.MigrationsHistoryTable("_efmigrationshistory", "public");
            }).UseSnakeCaseNamingConvention();
            options.EnableThreadSafetyChecks(false);
        }, Configuration.GetValue(nameof(MareConfigurationBase.DbContextPoolSize), 1024));

        services.AddSingleton(m => new MareMetrics(m.GetService<ILogger<MareMetrics>>(), new List<string> { },
        new List<string> { }));

        var redis = mareConfig.GetValue(nameof(ServerConfiguration.RedisConnectionString), string.Empty);
        var options = ConfigurationOptions.Parse(redis);
        options.ClientName = "Mare";
        options.ChannelPrefix = "UserData";
        ConnectionMultiplexer connectionMultiplexer = ConnectionMultiplexer.Connect(options);
        services.AddSingleton<IConnectionMultiplexer>(connectionMultiplexer);

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
        var redisConnection = mareConfig.GetValue(nameof(MareConfigurationBase.RedisConnectionString), string.Empty);
        signalRServiceBuilder.AddStackExchangeRedis(redisConnection, options => { });

        services.Configure<ServicesConfiguration>(Configuration.GetRequiredSection("MareSynchronos"));
        services.Configure<ServerConfiguration>(Configuration.GetRequiredSection("MareSynchronos"));
        services.Configure<MareConfigurationBase>(Configuration.GetRequiredSection("MareSynchronos"));
        services.AddSingleton(Configuration);
        services.AddSingleton<ServerTokenGenerator>();
        services.AddSingleton<DiscordBotServices>();
        services.AddHostedService<DiscordBot>();
        services.AddSingleton<IConfigurationService<ServicesConfiguration>, MareConfigurationServiceServer<ServicesConfiguration>>();
        services.AddSingleton<IConfigurationService<ServerConfiguration>, MareConfigurationServiceClient<ServerConfiguration>>();
        services.AddSingleton<IConfigurationService<MareConfigurationBase>, MareConfigurationServiceClient<MareConfigurationBase>>();

        services.AddHostedService(p => (MareConfigurationServiceClient<MareConfigurationBase>)p.GetService<IConfigurationService<MareConfigurationBase>>());
        services.AddHostedService(p => (MareConfigurationServiceClient<ServerConfiguration>)p.GetService<IConfigurationService<ServerConfiguration>>());
    }
}