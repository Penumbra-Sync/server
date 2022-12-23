using MareSynchronosShared.Utils;
using System.Text;

namespace MareSynchronosServices;

public class ServicesConfiguration : MareConfigurationBase
{
    public string DiscordBotToken { get; set; } = string.Empty;
    public bool PurgeUnusedAccounts { get; set; } = false;
    public int PurgeUnusedAccountsPeriodInDays { get; set; } = 14;
    public int MaxExistingGroupsByUser { get; set; } = 3;

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine(base.ToString());
        sb.AppendLine($"{nameof(DiscordBotToken)} => {DiscordBotToken}");
        sb.AppendLine($"{nameof(PurgeUnusedAccounts)} => {PurgeUnusedAccounts}");
        sb.AppendLine($"{nameof(PurgeUnusedAccountsPeriodInDays)} => {PurgeUnusedAccountsPeriodInDays}");
        sb.AppendLine($"{nameof(MaxExistingGroupsByUser)} => {MaxExistingGroupsByUser}");
        return sb.ToString();
    }
}
