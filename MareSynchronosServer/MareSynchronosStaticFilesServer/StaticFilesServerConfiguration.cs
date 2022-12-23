using MareSynchronosShared.Utils;
using System.Text;

namespace MareSynchronosStaticFilesServer;

public class StaticFilesServerConfiguration : MareConfigurationAuthBase
{
    public int ForcedDeletionOfFilesAfterHours { get; set; } = -1;
    public double CacheSizeHardLimitInGiB { get; set; } = -1;
    public int UnusedFileRetentionPeriodInDays { get; set; } = -1;
    public string CacheDirectory { get; set; }
    public Uri? RemoteCacheSourceUri { get; set; } = null;

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine(base.ToString());
        sb.AppendLine($"{nameof(ForcedDeletionOfFilesAfterHours)} => {ForcedDeletionOfFilesAfterHours}");
        sb.AppendLine($"{nameof(CacheSizeHardLimitInGiB)} => {CacheSizeHardLimitInGiB}");
        sb.AppendLine($"{nameof(UnusedFileRetentionPeriodInDays)} => {UnusedFileRetentionPeriodInDays}");
        sb.AppendLine($"{nameof(CacheDirectory)} => {CacheDirectory}");
        sb.AppendLine($"{nameof(RemoteCacheSourceUri)} => {RemoteCacheSourceUri}");
        return sb.ToString();
    }
}
