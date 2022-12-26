using System.Text;

namespace MareSynchronosShared.Utils;

public class MareConfigurationAuthBase : MareConfigurationBase
{
    public Uri MainServerGrpcAddress { get; set; } = null;
    [RemoteConfiguration]
    public int FailedAuthForTempBan { get; set; } = 5;
    [RemoteConfiguration]
    public int TempBanDurationInMinutes { get; set; } = 5;
    [RemoteConfiguration]
    public List<string> WhitelistedIps { get; set; } = new();

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine(base.ToString());
        sb.AppendLine($"{nameof(MainServerGrpcAddress)} => {MainServerGrpcAddress}");
        sb.AppendLine($"{nameof(FailedAuthForTempBan)} => {FailedAuthForTempBan}");
        sb.AppendLine($"{nameof(TempBanDurationInMinutes)} => {TempBanDurationInMinutes}");
        sb.AppendLine($"{nameof(WhitelistedIps)} => {string.Join(", ", WhitelistedIps)}");
        return sb.ToString();
    }
}