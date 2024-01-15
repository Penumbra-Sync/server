using MareSynchronosShared.Utils;
using System.Text;

namespace MareSynchronosStaticFilesServer;

public class StaticFilesServerConfiguration : MareConfigurationBase
{
    public bool IsDistributionNode { get; set; } = false;
    public Uri? MainFileServerAddress { get; set; } = null;
    public int ForcedDeletionOfFilesAfterHours { get; set; } = -1;
    public double CacheSizeHardLimitInGiB { get; set; } = -1;
    public int UnusedFileRetentionPeriodInDays { get; set; } = 14;
    public string CacheDirectory { get; set; }
    public int DownloadQueueSize { get; set; } = 50;
    public int DownloadTimeoutSeconds { get; set; } = 5;
    public int DownloadQueueReleaseSeconds { get; set; } = 15;
    public int DownloadQueueClearLimit { get; set; } = 15000;
    [RemoteConfiguration]
    public Uri CdnFullUrl { get; set; } = null;
    [RemoteConfiguration]
    public List<CdnShardConfiguration> CdnShardConfiguration { get; set; } = new();
    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine(base.ToString());
        sb.AppendLine($"{nameof(MainFileServerAddress)} => {MainFileServerAddress}");
        sb.AppendLine($"{nameof(ForcedDeletionOfFilesAfterHours)} => {ForcedDeletionOfFilesAfterHours}");
        sb.AppendLine($"{nameof(CacheSizeHardLimitInGiB)} => {CacheSizeHardLimitInGiB}");
        sb.AppendLine($"{nameof(UnusedFileRetentionPeriodInDays)} => {UnusedFileRetentionPeriodInDays}");
        sb.AppendLine($"{nameof(CacheDirectory)} => {CacheDirectory}");
        sb.AppendLine($"{nameof(DownloadQueueSize)} => {DownloadQueueSize}");
        sb.AppendLine($"{nameof(DownloadQueueReleaseSeconds)} => {DownloadQueueReleaseSeconds}");
        sb.AppendLine($"{nameof(CdnShardConfiguration)} => {string.Join(", ", CdnShardConfiguration)}");
        return sb.ToString();
    }
}
