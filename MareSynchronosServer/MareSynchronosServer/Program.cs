using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using System.Linq;
using MareSynchronosServer.Data;
using Microsoft.Extensions.DependencyInjection;

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
                    webBuilder.UseStartup<Startup>();
                });
    }
}
