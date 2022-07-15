using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using System.Linq;
using MareSynchronosServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var hostBuilder = CreateHostBuilder(args);
            var host = hostBuilder.Build();

            using (var scope = host.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                var context = services.GetRequiredService<MareDbContext>();
                context.Database.Migrate();
                context.SaveChanges();

                // clean up residuals
                var users = context.Users.Where(u => u.CharacterIdentification != null);
                foreach (var user in users)
                {
                    user.CharacterIdentification = string.Empty;
                }
                var looseFiles = context.Files.Where(f => f.Uploaded == false);
                context.RemoveRange(looseFiles);
                context.SaveChanges();

                MareMetrics.InitializeMetrics(context, services.GetRequiredService<IConfiguration>());
            }

            host.Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseSystemd()
                .UseConsoleLifetime()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseContentRoot(AppContext.BaseDirectory);
                    webBuilder.ConfigureLogging((ctx, builder) =>
                    {
                        builder.AddSimpleConsole(options =>
                        {
                            options.SingleLine = true;
                            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                        });
                        builder.AddConfiguration(ctx.Configuration.GetSection("Logging"));
                        builder.AddFile(o => o.RootPath = AppContext.BaseDirectory);
                    });
                    webBuilder.UseStartup<Startup>();
                });
    }
}
