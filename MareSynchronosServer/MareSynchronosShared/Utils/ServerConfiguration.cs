using System.Text;

namespace MareSynchronosShared.Utils;

public class ServerConfiguration : MareConfigurationAuthBase
{
    public string RedisConnectionString { get; set; } = string.Empty;

    [RemoteConfiguration]
    public Uri CdnFullUrl { get; set; } = null;
    [RemoteConfiguration]
    public Uri StaticFileServiceAddress { get; set; } = null;
    [RemoteConfiguration]
    public int MaxExistingGroupsByUser { get; set; } = 3;
    [RemoteConfiguration]
    public int MaxJoinedGroupsByUser { get; set; } = 6;
    [RemoteConfiguration]
    public int MaxGroupUserCount { get; set; } = 100;
    [RemoteConfiguration]
    public bool PurgeUnusedAccounts { get; set; } = false;
    [RemoteConfiguration]
    public int PurgeUnusedAccountsPeriodInDays { get; set; } = 14;

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine(base.ToString());
        sb.AppendLine($"{nameof(MainServerGrpcAddress)} => {MainServerGrpcAddress}");
        sb.AppendLine($"{nameof(CdnFullUrl)} => {CdnFullUrl}");
        sb.AppendLine($"{nameof(StaticFileServiceAddress)} => {StaticFileServiceAddress}");
        sb.AppendLine($"{nameof(RedisConnectionString)} => {RedisConnectionString}");
        sb.AppendLine($"{nameof(MaxExistingGroupsByUser)} => {MaxExistingGroupsByUser}");
        sb.AppendLine($"{nameof(MaxJoinedGroupsByUser)} => {MaxJoinedGroupsByUser}");
        sb.AppendLine($"{nameof(MaxGroupUserCount)} => {MaxGroupUserCount}");
        sb.AppendLine($"{nameof(PurgeUnusedAccounts)} => {PurgeUnusedAccounts}");
        sb.AppendLine($"{nameof(PurgeUnusedAccountsPeriodInDays)} => {PurgeUnusedAccountsPeriodInDays}");
        return sb.ToString();
    }
}
