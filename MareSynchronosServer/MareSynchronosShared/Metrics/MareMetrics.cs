using System.IO;
using System.Linq;
using MareSynchronosShared.Data;
using Microsoft.Extensions.Configuration;
using Prometheus;

namespace MareSynchronosServer.Metrics
{
    public class MareMetrics
    {
        public static readonly Counter InitializedConnections =
            Prometheus.Metrics.CreateCounter("mare_initialized_connections", "Initialized Connections");
        public static readonly Gauge Connections =
            Prometheus.Metrics.CreateGauge("mare_unauthorized_connections", "Unauthorized Connections");
        public static readonly Gauge AuthorizedConnections =
            Prometheus.Metrics.CreateGauge("mare_authorized_connections", "Authorized Connections");
        public static readonly Gauge AvailableWorkerThreads = Prometheus.Metrics.CreateGauge("mare_available_threadpool", "Available Threadpool Workers");
        public static readonly Gauge AvailableIOWorkerThreads = Prometheus.Metrics.CreateGauge("mare_available_threadpool_io", "Available Threadpool IO Workers");

        public static readonly Gauge UsersRegistered = Prometheus.Metrics.CreateGauge("mare_users_registered", "Total Registrations");

        public static readonly Gauge Pairs = Prometheus.Metrics.CreateGauge("mare_pairs", "Total Pairs");
        public static readonly Gauge PairsPaused = Prometheus.Metrics.CreateGauge("mare_pairs_paused", "Total Paused Pairs");

        public static readonly Gauge FilesTotal = Prometheus.Metrics.CreateGauge("mare_files", "Total uploaded files");
        public static readonly Gauge FilesTotalSize =
            Prometheus.Metrics.CreateGauge("mare_files_size", "Total uploaded files (bytes)");

        public static readonly Counter UserPushData = Prometheus.Metrics.CreateCounter("mare_user_push", "Users pushing data");
        public static readonly Counter UserPushDataTo =
            Prometheus.Metrics.CreateCounter("mare_user_push_to", "Users Receiving Data");

        public static readonly Counter UserDownloadedFiles =
            Prometheus.Metrics.CreateCounter("mare_user_downloaded_files", "Total Downloaded Files by Users");
        public static readonly Counter UserDownloadedFilesSize =
            Prometheus.Metrics.CreateCounter("mare_user_downloaded_files_size", "Total Downloaded Files Size by Users");

        public static readonly Gauge
            CPUUsage = Prometheus.Metrics.CreateGauge("mare_cpu_usage", "Total calculated CPU usage in %");
        public static readonly Gauge RAMUsage =
            Prometheus.Metrics.CreateGauge("mare_ram_usage", "Total calculated RAM usage in bytes for Mare + MSSQL");
        public static readonly Gauge NetworkOut = Prometheus.Metrics.CreateGauge("mare_network_out", "Network out in byte/s");
        public static readonly Gauge NetworkIn = Prometheus.Metrics.CreateGauge("mare_network_in", "Network in in byte/s");
        public static readonly Counter AuthenticationRequests = Prometheus.Metrics.CreateCounter("mare_auth_requests", "Mare Authentication Requests");
        public static readonly Counter AuthenticationCacheHits = Prometheus.Metrics.CreateCounter("mare_auth_requests_cachehit", "Mare Authentication Requests Cache Hits");
        public static readonly Counter AuthenticationFailures = Prometheus.Metrics.CreateCounter("mare_auth_requests_fail", "Mare Authentication Requests Failed");
        public static readonly Counter AuthenticationSuccesses = Prometheus.Metrics.CreateCounter("mare_auth_requests_success", "Mare Authentication Requests Success");

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
