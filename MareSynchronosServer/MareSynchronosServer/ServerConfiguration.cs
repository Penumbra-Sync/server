using MareSynchronosShared.Utils;
using System;
using System.Text;

namespace MareSynchronosServer;

public class ServerConfiguration : MareConfigurationAuthBase
{
    public Uri CdnFullUrl { get; set; } = null;
    public Uri ServiceAddress { get; set; } = null;
    public Uri StaticFileServiceAddress { get; set; } = null;
    public string RedisConnectionString { get; set; } = string.Empty;
    public int MaxExistingGroupsByUser { get; set; } = 3;
    public int MaxJoinedGroupsByUser { get; set; } = 6;
    public int MaxGroupUserCount { get; set; } = 100;
    public string ShardName { get; set; } = string.Empty;

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine(base.ToString());
        sb.AppendLine($"{nameof(ShardName)} => {ShardName}");
        sb.AppendLine($"{nameof(CdnFullUrl)} => {CdnFullUrl}");
        sb.AppendLine($"{nameof(ServiceAddress)} => {ServiceAddress}");
        sb.AppendLine($"{nameof(StaticFileServiceAddress)} => {StaticFileServiceAddress}");
        sb.AppendLine($"{nameof(RedisConnectionString)} => {RedisConnectionString}");
        sb.AppendLine($"{nameof(MaxExistingGroupsByUser)} => {MaxExistingGroupsByUser}");
        sb.AppendLine($"{nameof(MaxJoinedGroupsByUser)} => {MaxJoinedGroupsByUser}");
        sb.AppendLine($"{nameof(MaxGroupUserCount)} => {MaxGroupUserCount}");
        return sb.ToString();
    }
}
