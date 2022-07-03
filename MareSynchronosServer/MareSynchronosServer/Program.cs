using System;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using System.Linq;
using System.Reflection;
using MareSynchronosServer.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var host = CreateHostBuilder(args).Build();

            using (var scope = host.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                var context = services.GetRequiredService<MareDbContext>();
                context.Database.EnsureCreated();
                var users = context.Users.Where(u => u.CharacterIdentification != null);
                foreach (var user in users)
                {
                    user.CharacterIdentification = string.Empty;
                }
                context.CharacterData.RemoveRange(context.CharacterData);
                var looseFiles = context.Files.Where(f => f.Uploaded == false);
                context.RemoveRange(looseFiles);
                context.SaveChanges();
            }

            host.Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
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
