using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;

namespace MareSynchronosStaticFilesServer;

public class Program
{
    public static void Main(string[] args)
    {
        var hostBuilder = CreateHostBuilder(args);
        var host = hostBuilder.Build();

        using (var scope = host.Services.CreateScope())
        {
            var options = host.Services.GetService<IConfigurationService<StaticFilesServerConfiguration>>();
            var optionsServer = host.Services.GetService<IConfigurationService<MareConfigurationBase>>();
            var logger = host.Services.GetService<ILogger<Program>>();
            logger.LogInformation("Loaded MareSynchronos Static Files Server Configuration (IsMain: {isMain})", options.IsMain);
            logger.LogInformation(options.ToString());
            logger.LogInformation("Loaded MareSynchronos Server Auth Configuration (IsMain: {isMain})", optionsServer.IsMain);
            logger.LogInformation(optionsServer.ToString());
        }

        host.Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args)
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddConsole();
        });
        var logger = loggerFactory.CreateLogger<Startup>();
        return Host.CreateDefaultBuilder(args)
            .UseSystemd()
            .UseConsoleLifetime()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseContentRoot(AppContext.BaseDirectory);
                webBuilder.UseStartup(ctx => new Startup(ctx.Configuration, logger));
            });
    }
}