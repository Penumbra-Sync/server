using Grpc.Net.Client.Configuration;
using Grpc.Net.ClientFactory;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Protos;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Prometheus;

namespace MareSynchronosStaticFilesServer;

public class Startup
{
    private bool _isMain;
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
        var mareSettings = Configuration.GetRequiredSection("MareSynchronos");
        _isMain = string.IsNullOrEmpty(mareSettings.GetValue(nameof(StaticFilesServerConfiguration.RemoteCacheSourceUri), string.Empty));
    }

    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        services.AddLogging();

        services.Configure<StaticFilesServerConfiguration>(Configuration.GetRequiredSection("MareSynchronos"));
        services.AddSingleton(Configuration);

        var mareConfig = Configuration.GetRequiredSection("MareSynchronos");

        services.AddControllers();

        services.AddSingleton(m => new MareMetrics(m.GetService<ILogger<MareMetrics>>(), new List<string>
        {
            MetricsAPI.CounterAuthenticationCacheHits,
            MetricsAPI.CounterAuthenticationFailures,
            MetricsAPI.CounterAuthenticationRequests,
            MetricsAPI.CounterAuthenticationSuccesses
        }, new List<string>
        {
            MetricsAPI.GaugeFilesTotalSize,
            MetricsAPI.GaugeFilesTotal,
            MetricsAPI.GaugeFilesUniquePastDay,
            MetricsAPI.GaugeFilesUniquePastDaySize,
            MetricsAPI.GaugeFilesUniquePastHour,
            MetricsAPI.GaugeFilesUniquePastHourSize
        }));
        services.AddSingleton<CachedFileProvider>();
        services.AddSingleton<FileStatisticsService>();

        services.AddHostedService(m => m.GetService<FileStatisticsService>());
        services.AddHostedService<FileCleanupService>();

        services.AddSingleton<SecretKeyAuthenticatorService>();
        services.AddDbContextPool<MareDbContext>(options =>
        {
            options.UseNpgsql(Configuration.GetConnectionString("DefaultConnection"), builder =>
            {
                builder.MigrationsHistoryTable("_efmigrationshistory", "public");
            }).UseSnakeCaseNamingConvention();
            options.EnableThreadSafetyChecks(false);
        }, mareConfig.GetValue(nameof(MareConfigurationBase.DbContextPoolSize), 1024));

        var noRetryConfig = new MethodConfig
        {
            Names = { MethodName.Default },
            RetryPolicy = null
        };

        services.AddGrpcClient<ConfigurationService.ConfigurationServiceClient>("FileServer", c =>
        {
            c.Address = new Uri(mareConfig.GetValue<string>(nameof(StaticFilesServerConfiguration.FileServerGrpcAddress)));
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
            c.Address = new Uri(mareConfig.GetValue<string>(nameof(StaticFilesServerConfiguration.MainServerGrpcAddress)));
        }).ConfigureChannel(c =>
        {
            c.ServiceConfig = new ServiceConfig { MethodConfigs = { noRetryConfig } };
            c.HttpHandler = new SocketsHttpHandler()
            {
                EnableMultipleHttp2Connections = true
            };
        });

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = SecretKeyAuthenticationHandler.AuthScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, SecretKeyAuthenticationHandler>(SecretKeyAuthenticationHandler.AuthScheme, options => { });
        services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        if (_isMain)
        {
            services.AddGrpc(o =>
            {
                o.MaxReceiveMessageSize = null;
            });

            services.AddSingleton<IConfigurationService<StaticFilesServerConfiguration>, MareConfigurationServiceServer<StaticFilesServerConfiguration>>();
        }
        else
        {
            services.AddSingleton<IConfigurationService<StaticFilesServerConfiguration>>(p => new MareConfigurationServiceClient<StaticFilesServerConfiguration>(
                p.GetRequiredService<ILogger<MareConfigurationServiceClient<StaticFilesServerConfiguration>>>(),
                p.GetRequiredService<IOptions<StaticFilesServerConfiguration>>(),
                p.GetRequiredService<GrpcClientFactory>(),
                "FileServer"));
        }

        services.AddSingleton<IConfigurationService<MareConfigurationAuthBase>>(p =>
             new MareConfigurationServiceClient<MareConfigurationAuthBase>(
                p.GetRequiredService<ILogger<MareConfigurationServiceClient<MareConfigurationAuthBase>>>(),
                p.GetService<IOptions<MareConfigurationAuthBase>>(),
                p.GetRequiredService<GrpcClientFactory>(), "MainServer")
        );
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseHttpLogging();

        app.UseRouting();

        //var metricServer = new KestrelMetricServer(4981);
        //metricServer.Start();

        app.UseHttpMetrics();

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseEndpoints(e =>
        {
            if (_isMain)
            {
                e.MapGrpcService<GrpcFileService>();
            }
            e.MapControllers();
        });
    }
}