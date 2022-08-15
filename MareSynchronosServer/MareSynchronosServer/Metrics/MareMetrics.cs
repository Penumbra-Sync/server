using System.IO;
using System.Linq;
using MareSynchronosServer.Data;
using Microsoft.Extensions.Configuration;
using Prometheus;

namespace MareSynchronosServer.Metrics
{
    public class MareMetrics
    {
        public static readonly LockedProxyCounter InitializedConnections =
            new(Prometheus.Metrics.CreateCounter("mare_initialized_connections", "Initialized Connections"));
        public static readonly LockedProxyGauge Connections =
            new(Prometheus.Metrics.CreateGauge("mare_unauthorized_connections", "Unauthorized Connections"));
        public static readonly LockedProxyGauge AuthorizedConnections =
            new(Prometheus.Metrics.CreateGauge("mare_authorized_connections", "Authorized Connections"));
        public static readonly LockedProxyGauge AvailableWorkerThreads = new(Prometheus.Metrics.CreateGauge("mare_available_threadpool", "Available Threadpool Workers"));
        public static readonly LockedProxyGauge AvailableIOWorkerThreads = new(Prometheus.Metrics.CreateGauge("mare_available_threadpool_io", "Available Threadpool IO Workers"));

        public static readonly LockedProxyGauge UsersRegistered = new(Prometheus.Metrics.CreateGauge("mare_users_registered", "Total Registrations"));

        public static readonly LockedProxyGauge Pairs = new(Prometheus.Metrics.CreateGauge("mare_pairs", "Total Pairs"));
        public static readonly LockedProxyGauge PairsPaused = new(Prometheus.Metrics.CreateGauge("mare_pairs_paused", "Total Paused Pairs"));

        public static readonly LockedProxyGauge FilesTotal = new(Prometheus.Metrics.CreateGauge("mare_files", "Total uploaded files"));
        public static readonly LockedProxyGauge FilesTotalSize =
            new(Prometheus.Metrics.CreateGauge("mare_files_size", "Total uploaded files (bytes)"));

        public static readonly LockedProxyCounter UserPushData = new(Prometheus.Metrics.CreateCounter("mare_user_push", "Users pushing data"));
        public static readonly LockedProxyCounter UserPushDataTo =
            new(Prometheus.Metrics.CreateCounter("mare_user_push_to", "Users Receiving Data"));

        public static readonly LockedProxyCounter UserDownloadedFiles =
            new(Prometheus.Metrics.CreateCounter("mare_user_downloaded_files", "Total Downloaded Files by Users"));
        public static readonly LockedProxyCounter UserDownloadedFilesSize =
            new(Prometheus.Metrics.CreateCounter("mare_user_downloaded_files_size", "Total Downloaded Files Size by Users"));

        public static readonly LockedProxyGauge
            CPUUsage = new(Prometheus.Metrics.CreateGauge("mare_cpu_usage", "Total calculated CPU usage in %"));
        public static readonly LockedProxyGauge RAMUsage =
            new(Prometheus.Metrics.CreateGauge("mare_ram_usage", "Total calculated RAM usage in bytes for Mare + MSSQL"));
        public static readonly LockedProxyGauge NetworkOut = new(Prometheus.Metrics.CreateGauge("mare_network_out", "Network out in byte/s"));
        public static readonly LockedProxyGauge NetworkIn = new(Prometheus.Metrics.CreateGauge("mare_network_in", "Network in in byte/s"));

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
