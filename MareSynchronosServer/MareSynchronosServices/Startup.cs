using MareSynchronosServices.Discord;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using MareSynchronosShared.Utils;
using Grpc.Net.Client.Configuration;
using MareSynchronosShared.Protos;
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
        new List<string>
        {
            MetricsAPI.GaugeUsersRegistered
        }));

        var noRetryConfig = new MethodConfig
        {
            Names = { MethodName.Default },
            RetryPolicy = null
        };

        services.AddGrpcClient<IdentificationService.IdentificationServiceClient>(c =>
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

        services.AddGrpcClient<ConfigurationService.ConfigurationServiceClient>(c =>
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
        services.AddSingleton<IConfigurationService<ServerConfiguration>, MareConfigurationServiceClient<ServerConfiguration>>();
        services.AddSingleton<IConfigurationService<MareConfigurationAuthBase>, MareConfigurationServiceClient<MareConfigurationAuthBase>>();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
#if !DEBUG
        var metricServer = new KestrelMetricServer(4982);
        metricServer.Start();
#endif
    }
}