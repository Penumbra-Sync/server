using System.Text;

namespace MareSynchronosShared.Utils.Configuration;

public class StaticFilesServerConfiguration : MareConfigurationBase
{
    public bool IsDistributionNode { get; set; } = false;
    public Uri MainFileServerAddress { get; set; } = null;
    public Uri DistributionFileServerAddress { get; set; } = null;
    public int ForcedDeletionOfFilesAfterHours { get; set; } = -1;
    public double CacheSizeHardLimitInGiB { get; set; } = -1;
    public int UnusedFileRetentionPeriodInDays { get; set; } = 14;
    public string CacheDirectory { get; set; }
    public int DownloadQueueSize { get; set; } = 50;
    public int DownloadTimeoutSeconds { get; set; } = 5;
    public int DownloadQueueReleaseSeconds { get; set; } = 15;
    public int DownloadQueueClearLimit { get; set; } = 15000;
    public int CleanupCheckInMinutes { get; set; } = 15;
    public bool UseColdStorage { get; set; } = false;
    public string ColdStorageDirectory { get; set; } = null;
    public double ColdStorageSizeHardLimitInGiB { get; set; } = -1;
    public int ColdStorageUnusedFileRetentionPeriodInDays { get; set; } = 30;
    [RemoteConfiguration]
    public double SpeedTestHoursRateLimit { get; set; } = 0.5;
    [RemoteConfiguration]
    public Uri CdnFullUrl { get; set; } = null;
    public ShardConfiguration? ShardConfiguration { get; set; } = null;
    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine(base.ToString());
        sb.AppendLine($"{nameof(MainFileServerAddress)} => {MainFileServerAddress}");
        sb.AppendLine($"{nameof(ForcedDeletionOfFilesAfterHours)} => {ForcedDeletionOfFilesAfterHours}");
        sb.AppendLine($"{nameof(CacheSizeHardLimitInGiB)} => {CacheSizeHardLimitInGiB}");
        sb.AppendLine($"{nameof(UseColdStorage)} => {UseColdStorage}");
        sb.AppendLine($"{nameof(ColdStorageDirectory)} => {ColdStorageDirectory}");
        sb.AppendLine($"{nameof(ColdStorageSizeHardLimitInGiB)} => {ColdStorageSizeHardLimitInGiB}");
        sb.AppendLine($"{nameof(ColdStorageUnusedFileRetentionPeriodInDays)} => {ColdStorageUnusedFileRetentionPeriodInDays}");
        sb.AppendLine($"{nameof(UnusedFileRetentionPeriodInDays)} => {UnusedFileRetentionPeriodInDays}");
        sb.AppendLine($"{nameof(CacheDirectory)} => {CacheDirectory}");
        sb.AppendLine($"{nameof(DownloadQueueSize)} => {DownloadQueueSize}");
        sb.AppendLine($"{nameof(DownloadQueueReleaseSeconds)} => {DownloadQueueReleaseSeconds}");
        return sb.ToString();
    }
}
