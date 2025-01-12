using MareSynchronosAuthService.Controllers;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Mvc.Controllers;
using StackExchange.Redis.Extensions.Core.Configuration;
using StackExchange.Redis.Extensions.System.Text.Json;
using StackExchange.Redis;
using System.Net;
using MareSynchronosAuthService.Services;
using MareSynchronosShared.RequirementHandlers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using MareSynchronosShared.Utils.Configuration;
using StackExchange.Redis.Extensions.Core.Abstractions;

namespace MareSynchronosAuthService;

public class Startup
{
    private readonly IConfiguration _configuration;
    private ILogger<Startup> _logger;

    public Startup(IConfiguration configuration, ILogger<Startup> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
    {
        var config = app.ApplicationServices.GetRequiredService<IConfigurationService<MareConfigurationBase>>();

        app.UseRouting();

        app.UseHttpMetrics();

        app.UseAuthentication();
        app.UseAuthorization();

        KestrelMetricServer metricServer = new KestrelMetricServer(config.GetValueOrDefault<int>(nameof(MareConfigurationBase.MetricsPort), 4985));
        metricServer.Start();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();

            foreach (var source in endpoints.DataSources.SelectMany(e => e.Endpoints).Cast<RouteEndpoint>())
            {
                if (source == null) continue;
                _logger.LogInformation("Endpoint: {url} ", source.RoutePattern.RawText);
            }
        });
    }

    public void ConfigureServices(IServiceCollection services)
    {
        var mareConfig = _configuration.GetRequiredSection("MareSynchronos");

        services.AddHttpContextAccessor();

        ConfigureRedis(services, mareConfig);

        services.AddSingleton<SecretKeyAuthenticatorService>();
        services.AddSingleton<GeoIPService>();

        services.AddHostedService(provider => provider.GetRequiredService<GeoIPService>());

        services.Configure<AuthServiceConfiguration>(_configuration.GetRequiredSection("MareSynchronos"));
        services.Configure<MareConfigurationBase>(_configuration.GetRequiredSection("MareSynchronos"));

        services.AddSingleton<ServerTokenGenerator>();

        ConfigureAuthorization(services);

        ConfigureDatabase(services, mareConfig);

        ConfigureConfigServices(services);

        ConfigureMetrics(services);

        services.AddHealthChecks();
        services.AddControllers().ConfigureApplicationPartManager(a =>
        {
            a.FeatureProviders.Remove(a.FeatureProviders.OfType<ControllerFeatureProvider>().First());
            a.FeatureProviders.Add(new AllowedControllersFeatureProvider(typeof(JwtController), typeof(OAuthController)));
        });
    }

    private static void ConfigureAuthorization(IServiceCollection services)
    {
        services.AddTransient<IAuthorizationHandler, RedisDbUserRequirementHandler>();
        services.AddTransient<IAuthorizationHandler, ValidTokenRequirementHandler>();
        services.AddTransient<IAuthorizationHandler, ExistingUserRequirementHandler>();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IConfigurationService<MareConfigurationBase>>((options, config) =>
            {
                options.TokenValidationParameters = new()
                {
                    ValidateIssuer = false,
                    ValidateLifetime = true,
                    ValidateAudience = false,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(config.GetValue<string>(nameof(MareConfigurationBase.Jwt)))),
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
            options.AddPolicy("OAuthToken", policy =>
            {
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                policy.AddRequirements(new ValidTokenRequirement());
                policy.AddRequirements(new ExistingUserRequirement());
                policy.RequireClaim(MareClaimTypes.OAuthLoginToken, "True");
            });
            options.AddPolicy("Authenticated", policy =>
            {
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ValidTokenRequirement());
            });
            options.AddPolicy("Identified", policy =>
            {
                policy.AddRequirements(new UserRequirement(UserRequirements.Identified));
                policy.AddRequirements(new ValidTokenRequirement());

            });
            options.AddPolicy("Admin", policy =>
            {
                policy.AddRequirements(new UserRequirement(UserRequirements.Identified | UserRequirements.Administrator));
                policy.AddRequirements(new ValidTokenRequirement());

            });
            options.AddPolicy("Moderator", policy =>
            {
                policy.AddRequirements(new UserRequirement(UserRequirements.Identified | UserRequirements.Moderator | UserRequirements.Administrator));
                policy.AddRequirements(new ValidTokenRequirement());
            });
            options.AddPolicy("Internal", new AuthorizationPolicyBuilder().RequireClaim(MareClaimTypes.Internal, "true").Build());
        });
    }

    private static void ConfigureMetrics(IServiceCollection services)
    {
        services.AddSingleton<MareMetrics>(m => new MareMetrics(m.GetService<ILogger<MareMetrics>>(), new List<string>
        {
            MetricsAPI.CounterAuthenticationCacheHits,
            MetricsAPI.CounterAuthenticationFailures,
            MetricsAPI.CounterAuthenticationRequests,
            MetricsAPI.CounterAuthenticationSuccesses,
        }, new List<string>
        {
            MetricsAPI.GaugeAuthenticationCacheEntries,
        }));
    }

    private void ConfigureRedis(IServiceCollection services, IConfigurationSection mareConfig)
    {
        // configure redis for SignalR
        var redisConnection = mareConfig.GetValue(nameof(ServerConfiguration.RedisConnectionString), string.Empty);
        var options = ConfigurationOptions.Parse(redisConnection);

        var endpoint = options.EndPoints[0];
        string address = "";
        int port = 0;
        
        if (endpoint is DnsEndPoint dnsEndPoint) { address = dnsEndPoint.Host; port = dnsEndPoint.Port; }
        if (endpoint is IPEndPoint ipEndPoint) { address = ipEndPoint.Address.ToString(); port = ipEndPoint.Port; }
        /*
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
        };*/

        var muxer = ConnectionMultiplexer.Connect(options);
        var db = muxer.GetDatabase();
        services.AddSingleton<IDatabase>(db);

        _logger.LogInformation("Setting up Redis to connect to {host}:{port}", address, port);
    }
    private void ConfigureConfigServices(IServiceCollection services)
    {
        services.AddSingleton<IConfigurationService<AuthServiceConfiguration>, MareConfigurationServiceServer<AuthServiceConfiguration>>();
        services.AddSingleton<IConfigurationService<MareConfigurationBase>, MareConfigurationServiceServer<MareConfigurationBase>>();
    }

    private void ConfigureDatabase(IServiceCollection services, IConfigurationSection mareConfig)
    {
        services.AddDbContextPool<MareDbContext>(options =>
        {
            options.UseNpgsql(_configuration.GetConnectionString("DefaultConnection"), builder =>
            {
                builder.MigrationsHistoryTable("_efmigrationshistory", "public");
                builder.MigrationsAssembly("MareSynchronosShared");
            }).UseSnakeCaseNamingConvention();
            options.EnableThreadSafetyChecks(false);
        }, mareConfig.GetValue(nameof(MareConfigurationBase.DbContextPoolSize), 1024));
        services.AddDbContextFactory<MareDbContext>(options =>
        {
            options.UseNpgsql(_configuration.GetConnectionString("DefaultConnection"), builder =>
            {
                builder.MigrationsHistoryTable("_efmigrationshistory", "public");
                builder.MigrationsAssembly("MareSynchronosShared");
            }).UseSnakeCaseNamingConvention();
            options.EnableThreadSafetyChecks(false);
        });
    }
}
