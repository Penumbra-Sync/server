using MareSynchronosServices.Discord;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using MareSynchronosShared.Utils;
using Grpc.Net.Client.Configuration;
using MareSynchronosShared.Protos;
using MareSynchronosShared.Services;
using Grpc.Net.ClientFactory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

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

        var noRetryConfig = new MethodConfig
        {
            Names = { MethodName.Default },
            RetryPolicy = null
        };

        var redis = mareConfig.GetValue(nameof(ServerConfiguration.RedisConnectionString), string.Empty);
        var options = ConfigurationOptions.Parse(redis);
        options.ClientName = "Mare";
        options.ChannelPrefix = "UserData";
        ConnectionMultiplexer connectionMultiplexer = ConnectionMultiplexer.Connect(options);
        services.AddSingleton<IConnectionMultiplexer>(connectionMultiplexer);

        services.AddGrpcClient<ConfigurationService.ConfigurationServiceClient>("MainServer", c =>
        {
            c.Address = new Uri(mareConfig.GetValue<string>(nameof(ServicesConfiguration.MainServerGrpcAddress)));
        }).ConfigureChannel(c =>
        {
            c.ServiceConfig = new ServiceConfig { MethodConfigs = { noRetryConfig } };
            c.HttpHandler = new SocketsHttpHandler()
            {
                EnableMultipleHttp2Connections = true
            };
        });

        services.AddGrpcClient<ClientMessageService.ClientMessageServiceClient>("MessageClient", c =>
        {
            c.Address = new Uri(mareConfig.GetValue<string>(nameof(ServicesConfiguration.MainServerGrpcAddress)));
        }).ConfigureChannel(c =>
        {
            c.ServiceConfig = new ServiceConfig { MethodConfigs = { noRetryConfig } };
            c.HttpHandler = new SocketsHttpHandler()
            {
                EnableMultipleHttp2Connections = true
            };
        });

        services.Configure<ServicesConfiguration>(Configuration.GetRequiredSection("MareSynchronos"));
        services.Configure<ServerConfiguration>(Configuration.GetRequiredSection("MareSynchronos"));
        services.Configure<MareConfigurationAuthBase>(Configuration.GetRequiredSection("MareSynchronos"));
        services.AddSingleton(Configuration);
        services.AddSingleton<DiscordBotServices>();
        services.AddHostedService<DiscordBot>();
        services.AddSingleton<IConfigurationService<ServicesConfiguration>, MareConfigurationServiceServer<ServicesConfiguration>>();
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

        services.AddHostedService(p => (MareConfigurationServiceClient<MareConfigurationAuthBase>)p.GetService<IConfigurationService<MareConfigurationAuthBase>>());
        services.AddHostedService(p => (MareConfigurationServiceClient<ServerConfiguration>)p.GetService<IConfigurationService<ServerConfiguration>>());
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        var config = app.ApplicationServices.GetRequiredService<IConfigurationService<MareConfigurationAuthBase>>();

        var metricServer = new KestrelMetricServer(config.GetValueOrDefault<int>(nameof(MareConfigurationBase.MetricsPort), 4982));
        metricServer.Start();
    }
}