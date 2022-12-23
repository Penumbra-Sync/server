using System.Text;

namespace MareSynchronosShared.Utils;

public class MareConfigurationBase
{
    public int DbContextPoolSize { get; set; } = 100;

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine($"{nameof(DbContextPoolSize)} => {DbContextPoolSize}");
        return sb.ToString();
    }
}

public class MareConfigurationAuthBase : MareConfigurationBase
{
    public int DbContextPoolSize { get; set; } = 100;
    public int FailedAuthForTempBan { get; set; } = 5;
    public int TempBanDurationInMinutes { get; set; } = 5;
    public List<string> WhitelistedIps { get; set; } = new();

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine($"{nameof(FailedAuthForTempBan)} => {FailedAuthForTempBan}");
        sb.AppendLine($"{nameof(TempBanDurationInMinutes)} => {TempBanDurationInMinutes}");
        sb.AppendLine($"{nameof(WhitelistedIps)} => {string.Join(", ", WhitelistedIps)}");
        return sb.ToString();
    }
}