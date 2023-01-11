using MareSynchronosShared.Utils;
using System.Text;

namespace MareSynchronosStaticFilesServer;

public class StaticFilesServerConfiguration : MareConfigurationBase
{
    public Uri FileServerGrpcAddress { get; set; } = null;
    public int ForcedDeletionOfFilesAfterHours { get; set; } = -1;
    public double CacheSizeHardLimitInGiB { get; set; } = -1;
    public int UnusedFileRetentionPeriodInDays { get; set; } = 14;
    public string CacheDirectory { get; set; }
    public Uri? RemoteCacheSourceUri { get; set; } = null;
    public Uri MainServerGrpcAddress { get; set; } = null;
    public int DownloadQueueSize { get; set; } = 50;
    public int DownloadTimeoutSeconds { get; set; } = 5;
    public int DownloadQueueReleaseSeconds { get; set; } = 15;
    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine(base.ToString());
        sb.AppendLine($"{nameof(FileServerGrpcAddress)} => {FileServerGrpcAddress}");
        sb.AppendLine($"{nameof(MainServerGrpcAddress)} => {MainServerGrpcAddress}");
        sb.AppendLine($"{nameof(ForcedDeletionOfFilesAfterHours)} => {ForcedDeletionOfFilesAfterHours}");
        sb.AppendLine($"{nameof(CacheSizeHardLimitInGiB)} => {CacheSizeHardLimitInGiB}");
        sb.AppendLine($"{nameof(UnusedFileRetentionPeriodInDays)} => {UnusedFileRetentionPeriodInDays}");
        sb.AppendLine($"{nameof(CacheDirectory)} => {CacheDirectory}");
        sb.AppendLine($"{nameof(RemoteCacheSourceUri)} => {RemoteCacheSourceUri}");
        sb.AppendLine($"{nameof(DownloadQueueSize)} => {DownloadQueueSize}");
        sb.AppendLine($"{nameof(DownloadQueueReleaseSeconds)} => {DownloadQueueReleaseSeconds}");
        return sb.ToString();
    }
}
