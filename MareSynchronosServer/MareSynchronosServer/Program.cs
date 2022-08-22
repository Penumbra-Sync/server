using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using MareSynchronosShared.Data;

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
                using var context = services.GetRequiredService<MareDbContext>();
                context.Database.Migrate();
                context.SaveChanges();

                // clean up residuals
                var users = context.Users;
                foreach (var user in users)
                {
                    user.CharacterIdentification = null;
                }
                var looseFiles = context.Files.Where(f => f.Uploaded == false);
                var unfinishedRegistrations = context.LodeStoneAuth.Where(c => c.StartedAt != null);
                context.RemoveRange(unfinishedRegistrations);
                context.RemoveRange(looseFiles);
                context.SaveChanges();
            }

            if (args.Length == 0 || args[0] != "dry")
            {
                host.Run();
            }
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
                        builder.AddConfiguration(ctx.Configuration.GetSection("Logging"));
                        builder.AddFile(o => o.RootPath = AppContext.BaseDirectory);
                    });
                    webBuilder.UseStartup<Startup>();
                });
    }
}
