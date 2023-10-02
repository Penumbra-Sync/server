﻿using System.Text;

namespace MareSynchronosShared.Utils;

public class ServicesConfiguration : MareConfigurationBase
{
    public string DiscordBotToken { get; set; } = string.Empty;
    public ulong? DiscordChannelForMessages { get; set; } = null;
    public ulong? DiscordChannelForReports { get; set; } = null;
    public ulong? DiscordChannelForCommands { get; set; } = null;
    public Uri MainServerGrpcAddress { get; set; } = null;
    public ulong[]? VanityRoles { get; set; } = null;

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine(base.ToString());
        sb.AppendLine($"{nameof(DiscordBotToken)} => {DiscordBotToken}");
        sb.AppendLine($"{nameof(MainServerGrpcAddress)} => {MainServerGrpcAddress}");
        sb.AppendLine($"{nameof(DiscordChannelForMessages)} => {DiscordChannelForMessages}");
        sb.AppendLine($"{nameof(DiscordChannelForReports)} => {DiscordChannelForReports}");
        sb.AppendLine($"{nameof(DiscordChannelForCommands)} => {DiscordChannelForCommands}");
        foreach (var role in VanityRoles)
        {
            sb.AppendLine($"{nameof(VanityRoles)} => {role}");
        }
        return sb.ToString();
    }
}