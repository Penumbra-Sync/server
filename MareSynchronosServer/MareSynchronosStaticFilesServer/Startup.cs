using Grpc.Net.Client.Configuration;
using Grpc.Net.ClientFactory;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Protos;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Prometheus;
using System.Text;

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
        services.Configure<MareConfigurationAuthBase>(Configuration.GetRequiredSection("MareSynchronos"));
        services.AddSingleton(Configuration);

        var mareConfig = Configuration.GetRequiredSection("MareSynchronos");

        services.AddControllers();

        services.AddSingleton(m => new MareMetrics(m.GetService<ILogger<MareMetrics>>(), new List<string>
        {
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

            services.AddHostedService(p => (MareConfigurationServiceClient<StaticFilesServerConfiguration>)p.GetService<IConfigurationService<StaticFilesServerConfiguration>>());
        }

        services.AddSingleton<IConfigurationService<MareConfigurationAuthBase>>(p =>
             new MareConfigurationServiceClient<MareConfigurationAuthBase>(
                p.GetRequiredService<ILogger<MareConfigurationServiceClient<MareConfigurationAuthBase>>>(),
                p.GetService<IOptions<MareConfigurationAuthBase>>(),
                p.GetRequiredService<GrpcClientFactory>(), "MainServer")
        );

        services.AddHostedService(p => (MareConfigurationServiceClient<MareConfigurationAuthBase>)p.GetService<IConfigurationService<MareConfigurationAuthBase>>());

        services.AddHealthChecks();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseHttpLogging();

        app.UseRouting();

        var config = app.ApplicationServices.GetRequiredService<IConfigurationService<MareConfigurationAuthBase>>();

        var metricServer = new KestrelMetricServer(config.GetValueOrDefault<int>(nameof(MareConfigurationBase.MetricsPort), 4981));
        metricServer.Start();

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
            e.MapHealthChecks("/health").WithMetadata(new AllowAnonymousAttribute());
        });
    }
}