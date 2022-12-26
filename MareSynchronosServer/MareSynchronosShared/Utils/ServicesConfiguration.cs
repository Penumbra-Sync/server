using System.Text;

namespace MareSynchronosShared.Utils;

public class ServicesConfiguration : MareConfigurationBase
{
    public string DiscordBotToken { get; set; } = string.Empty;
    public Uri MainServerGrpcAddress { get; set; } = null;

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine(base.ToString());
        sb.AppendLine($"{nameof(DiscordBotToken)} => {DiscordBotToken}");
        sb.AppendLine($"{nameof(MainServerGrpcAddress)} => {MainServerGrpcAddress}");
        return sb.ToString();
    }
}
