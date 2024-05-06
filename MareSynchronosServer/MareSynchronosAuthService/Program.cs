namespace MareSynchronosAuthService;

public class Program
{
    public static void Main(string[] args)
    {
        var hostBuilder = CreateHostBuilder(args);
        using var host = hostBuilder.Build();
        try
        {
            host.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    public static IHostBuilder CreateHostBuilder(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
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
                webBuilder.ConfigureLogging((ctx, builder) =>
                {
                    builder.AddConfiguration(ctx.Configuration.GetSection("Logging"));
                    builder.AddFile(o => o.RootPath = AppContext.BaseDirectory);
                });
                webBuilder.UseStartup(ctx => new Startup(ctx.Configuration, logger));
            });
    }
}
