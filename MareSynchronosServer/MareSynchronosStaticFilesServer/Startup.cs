using Grpc.Net.Client.Configuration;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Prometheus;

namespace MareSynchronosStaticFilesServer;

public class Startup
{
    private bool _isMain;
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
        var mareSettings = Configuration.GetRequiredSection("MareSynchronos");
        _isMain = string.IsNullOrEmpty(mareSettings.GetValue("RemoteCacheSourceUri", string.Empty));
    }

    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        services.AddTransient(_ => Configuration);

        services.AddLogging();

        var mareSettings = Configuration.GetRequiredSection("MareSynchronos");

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
        }, mareSettings.GetValue("DbContextPoolSize", 1024));

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = SecretKeyAuthenticationHandler.AuthScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, SecretKeyAuthenticationHandler>(SecretKeyAuthenticationHandler.AuthScheme, options => { });
        services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddGrpc(o =>
        {
            o.MaxReceiveMessageSize = null;
        });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseHttpLogging();

        app.UseRouting();

        var metricServer = new KestrelMetricServer(4981);
        metricServer.Start();

        app.UseHttpMetrics();

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseEndpoints(e =>
        {
            if(_isMain)
                e.MapGrpcService<GrpcFileService>();
            e.MapControllers();
        });
    }
}