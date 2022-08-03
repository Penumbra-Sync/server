using System;
using MareSynchronos.API;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MareSynchronosServer.Authentication;
using MareSynchronosServer.Data;
using MareSynchronosServer.Hubs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Prometheus;
using WebSocketOptions = Microsoft.AspNetCore.Builder.WebSocketOptions;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Authorization;
using MareSynchronosServer.Discord;
using AspNetCoreRateLimit;

namespace MareSynchronosServer
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }


        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSignalR(hubOptions =>
            {
                hubOptions.MaximumReceiveMessageSize = long.MaxValue;
                hubOptions.EnableDetailedErrors = true;
                hubOptions.MaximumParallelInvocationsPerClient = 10;
                hubOptions.StreamBufferCapacity = 200;
            });

            services.AddMemoryCache();

            services.Configure<IpRateLimitOptions>(Configuration.GetSection("IpRateLimiting"));
            services.Configure<IpRateLimitPolicies>(Configuration.GetSection("IpRateLimitPolicies"));

            services.AddInMemoryRateLimiting();

            services.AddSingleton<SystemInfoService, SystemInfoService>();
            services.AddSingleton<IUserIdProvider, IdBasedUserIdProvider>();
            services.AddTransient(_ => Configuration);

            services.AddDbContextPool<MareDbContext>(options =>
            {
                options.UseNpgsql(Configuration.GetConnectionString("DefaultConnection"), builder =>
                {
                    builder.MigrationsHistoryTable("_efmigrationshistory", "public");
                }).UseSnakeCaseNamingConvention();
            });

            services.AddHostedService<FileCleanupService>();
            services.AddHostedService(provider => provider.GetService<SystemInfoService>());
            services.AddHostedService<DiscordBot>();
            services.AddHttpContextAccessor();

            services.AddDatabaseDeveloperPageExceptionFilter();
            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = SecretKeyAuthenticationHandler.AuthScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, SecretKeyAuthenticationHandler>(SecretKeyAuthenticationHandler.AuthScheme, options => { });
            services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

            services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
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
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseIpRateLimiting();

            app.UseStaticFiles();
            app.UseHttpLogging();

            app.UseRouting();
            var webSocketOptions = new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(10),
            };

            app.UseHttpMetrics();
            app.UseWebSockets(webSocketOptions);

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseStaticFiles(new StaticFileOptions()
            {
                FileProvider = new PhysicalFileProvider(Configuration["CacheDirectory"]),
                RequestPath = "/cache",
                ServeUnknownFileTypes = true
            });

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapHub<MareHub>(Api.Path, options =>
                {
                    options.ApplicationMaxBufferSize = 5242880;
                    options.TransportMaxBufferSize = 5242880;
                    options.Transports = HttpTransportType.WebSockets;
                });

                endpoints.MapMetrics();
            });
        }
    }
}
