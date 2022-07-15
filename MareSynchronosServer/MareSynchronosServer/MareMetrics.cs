using System.IO;
using System.Linq;
using MareSynchronosServer.Data;
using Microsoft.Extensions.Configuration;
using Prometheus;

namespace MareSynchronosServer
{
    public class MareMetrics
    {
        public static readonly Counter InitializedConnections =
            Metrics.CreateCounter("mare_initialized_connections", "Initialized Connections");
        public static readonly Gauge UnauthorizedConnections =
            Metrics.CreateGauge("mare_unauthorized_connections", "Unauthorized Connections");
        public static readonly Gauge AuthorizedConnections =
            Metrics.CreateGauge("mare_authorized_connections", "Authorized Connections");

        public static readonly Gauge UsersRegistered = Metrics.CreateGauge("mare_users_registered", "Total Registrations");

        public static readonly Gauge Pairs = Metrics.CreateGauge("mare_pairs", "Total Pairs");
        public static readonly Gauge PairsPaused = Metrics.CreateGauge("mare_pairs_paused", "Total Paused Pairs");

        public static readonly Gauge FilesTotal = Metrics.CreateGauge("mare_files", "Total uploaded files");
        public static readonly Gauge FilesTotalSize =
            Metrics.CreateGauge("mare_files_size", "Total uploaded files (bytes)");

        public static readonly Counter UserPushData = Metrics.CreateCounter("mare_user_push", "Users pushing data");
        public static readonly Counter UserPushDataTo =
            Metrics.CreateCounter("mare_user_push_to", "Users Receiving Data");

        public static readonly Counter UserDownloadedFiles = Metrics.CreateCounter("mare_user_downloaded_files", "Total Downloaded Files by Users");
        public static readonly Counter UserDownloadedFilesSize = Metrics.CreateCounter("mare_user_downloaded_files_size", "Total Downloaded Files Size by Users");

        public static readonly Gauge
            CPUUsage = Metrics.CreateGauge("mare_cpu_usage", "Total calculated CPU usage in %");
        public static readonly Gauge RAMUsage =
            Metrics.CreateGauge("mare_ram_usage", "Total calculated RAM usage in bytes for Mare + MSSQL");
        public static readonly Gauge NetworkOut = Metrics.CreateGauge("mare_network_out", "Network out in byte/s");
        public static readonly Gauge NetworkIn = Metrics.CreateGauge("mare_network_in", "Network in in byte/s");

        public static void InitializeMetrics(MareDbContext dbContext, IConfiguration configuration)
        {
            UsersRegistered.IncTo(dbContext.Users.Count());
            Pairs.IncTo(dbContext.ClientPairs.Count());
            PairsPaused.IncTo(dbContext.ClientPairs.Count(p => p.IsPaused));
            FilesTotal.IncTo(dbContext.Files.Count());
            FilesTotalSize.IncTo(Directory.EnumerateFiles(configuration["CacheDirectory"]).Sum(f => new FileInfo(f).Length));
        }
    }
}
