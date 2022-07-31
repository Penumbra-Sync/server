using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using System.Linq;
using MareSynchronosServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MareSynchronosServer.Metrics;
using MareSynchronosServer.Models;
using System.Collections.Generic;

namespace MareSynchronosServer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var hostBuilder = CreateHostBuilder(args);
            var host = hostBuilder.Build();

            System.Threading.ThreadPool.GetMaxThreads(out int worker, out int io);
            Console.WriteLine($"Before: Worker threads {worker}, IO threads {io}");

            using (var scope = host.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                var context = services.GetRequiredService<MareDbContext>();
                context.Database.Migrate();
                context.SaveChanges();

                // clean up residuals
                var users = context.Users;
                foreach (var user in users)
                {
                    user.CharacterIdentification = null;
                }
                var looseFiles = context.Files.Where(f => f.Uploaded == false);
                context.RemoveRange(looseFiles);
                context.SaveChanges();

                System.Threading.ThreadPool.SetMaxThreads(worker, context.Users.Count() * 5);
                System.Threading.ThreadPool.GetMaxThreads(out int workerNew, out int ioNew);
                Console.WriteLine($"After: Worker threads {workerNew}, IO threads {ioNew}");

                MareMetrics.InitializeMetrics(context, services.GetRequiredService<IConfiguration>());
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
