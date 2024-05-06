using Discord;
using Discord.Interactions;
using MareSynchronosShared.Utils.Configuration;
using System.Text.Json;

namespace MareSynchronosServices.Discord;

public partial class MareWizardModule : InteractionModuleBase
{
    private const int _totalAprilFoolsRoles = 200;
    private const string _persistentFileName = "april2024.json";

    private static readonly SemaphoreSlim _fileSemaphore = new(1, 1);

    [ComponentInteraction("wizard-fools")]
    public async Task ComponentFools()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentFools), Context.Interaction.User.Id);

        EmbedBuilder eb = new();
        eb.WithTitle("WorryCoin™ and MareToken© Balance");
        eb.WithColor(Color.Gold);
        eb.WithDescription("You currently have" + Environment.NewLine + Environment.NewLine
            + "**200000** MaTE©" + Environment.NewLine
            + "**0** WorryCoin™" + Environment.NewLine + Environment.NewLine
            + "You have no payment method set up. Press the button below to add a payment method.");
        ComponentBuilder cb = new();
        AddHome(cb);
        cb.WithButton("Add Payment Method", "wizard-fools-start", ButtonStyle.Primary, emote: new Emoji("💲"));
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-fools-start")]
    public async Task ComponentFoolsStart()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentFoolsStart), Context.Interaction.User.Id);

        EmbedBuilder eb = new();
        var user = await Context.Guild.GetUserAsync(Context.User.Id).ConfigureAwait(false);
        bool userIsInPermanentVanityRole = _botServices.VanityRoles.Where(v => !v.Value.Contains('$', StringComparison.Ordinal))
            .Select(v => v.Key).Any(u => user.RoleIds.Contains(u.Id)) || !_botServices.VanityRoles.Any();
        ComponentBuilder cb = new();
        AddHome(cb);

        var participatedUsers = await GetParticipants().ConfigureAwait(false);
        var remainingRoles = _totalAprilFoolsRoles - participatedUsers.Count(c => c.Value == true);

        if (userIsInPermanentVanityRole)
        {
            eb.WithColor(Color.Green);
            eb.WithTitle("Happy April Fools!");
            eb.WithDescription("Thank you for participating in Mares 2024 April Fools event."
                + Environment.NewLine + Environment.NewLine
                + "As you might have already guessed from the post, nothing that was written there had any truth behind it."
                + Environment.NewLine + Environment.NewLine
                + "This entire thing was a jab at the ridiculousness of cryptocurrency, microtransactions and games featuring multiple currencies. I hope you enjoyed the announcement post!"
                + Environment.NewLine + Environment.NewLine
                + "__As you already have a role that gives you a permanent Vanity ID, you cannot win another one here. "
                + "However, tell your friends as this bot will give them a chance to win one of " + _totalAprilFoolsRoles + " lifetime vanity roles.__"
                + Environment.NewLine + Environment.NewLine
                + "The giveaway is active until <t:" + (new DateTime(2024, 04, 01, 23, 59, 59, DateTimeKind.Utc).Subtract(DateTime.UnixEpoch).TotalSeconds) + ":f>.");
        }
        else if (participatedUsers.ContainsKey(Context.User.Id))
        {
            eb.WithColor(Color.Orange);
            eb.WithTitle("Happy April Fools!");
            eb.WithDescription("Thank you for participating in Mares 2024 April Fools event."
                + Environment.NewLine + Environment.NewLine
                + "As you might have already guessed from the post, nothing that was written there had any truth behind it."
                + Environment.NewLine + Environment.NewLine
                + "This entire thing was a jab at the ridiculousness of cryptocurrency, microtransactions and games featuring multiple currencies. I hope you enjoyed the announcement post!"
                + Environment.NewLine + Environment.NewLine
                + "__You already participated in the giveaway of the permanent Vanity roles and therefore cannot participate again. Better luck next time!__");
        }
        else if (remainingRoles > 0)
        {
            eb.WithColor(Color.Green);
            eb.WithTitle("Happy April Fools!");
            eb.WithDescription("Thank you for participating in Mares 2024 April Fools event."
                + Environment.NewLine + Environment.NewLine
                + "As you might have already guessed from the post, nothing that was written there had any truth behind it."
                + Environment.NewLine + Environment.NewLine
                + "This entire thing was a jab at the ridiculousness of cryptocurrency, microtransactions and games featuring multiple currencies. I hope you enjoyed the announcement post!"
                + Environment.NewLine + Environment.NewLine
                + "You have currently no permanent role that allows you to set a Vanity ID, however I am giving away a total of " + _totalAprilFoolsRoles + " permanent vanity roles "
                + "(" + remainingRoles + " still remain) and you can win one using this bot!"
                + Environment.NewLine + Environment.NewLine
                + "To win you simply have to pick one of the buttons labeled \"Win\" below this post. Which button will win is random. "
                + "There is a 1 in 5 chance that you can win the role. __You can only participate once.__"
                + Environment.NewLine + Environment.NewLine
                + "The giveaway is active until <t:" + (new DateTime(2024, 04, 01, 23, 59, 59, DateTimeKind.Utc).Subtract(DateTime.UnixEpoch).TotalSeconds) + ":f>.");
            cb.WithButton("Win", "wizard-fools-win:1", ButtonStyle.Primary, new Emoji("1️⃣"));
            cb.WithButton("Win", "wizard-fools-win:2", ButtonStyle.Primary, new Emoji("2️⃣"));
            cb.WithButton("Win", "wizard-fools-win:3", ButtonStyle.Primary, new Emoji("3️⃣"));
            cb.WithButton("Win", "wizard-fools-win:4", ButtonStyle.Primary, new Emoji("4️⃣"));
            cb.WithButton("Win", "wizard-fools-win:5", ButtonStyle.Primary, new Emoji("5️⃣"));
        }
        else
        {
            eb.WithColor(Color.Orange);
            eb.WithTitle("Happy April Fools!");
            eb.WithDescription("Thank you for participating in Mares 2024 April Fools event."
                + Environment.NewLine + Environment.NewLine
                + "As you might have already guessed from the post, nothing that was written there had any truth behind it."
                + Environment.NewLine + Environment.NewLine
                + "This entire thing was a jab at the ridiculousness of cryptocurrency, microtransactions and games featuring multiple currencies. I hope you enjoyed the announcement post!"
                + Environment.NewLine + Environment.NewLine
                + "__I have been giving away " + _totalAprilFoolsRoles + " permanent Vanity ID roles for this server, however you are sadly too late as they ran out by now. "
                + "Better luck next year with whatever I will come up with!__");
        }
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-fools-win:*")]
    public async Task ComponentFoolsWin(int number)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentFoolsWin), Context.Interaction.User.Id);

        var winningNumber = new Random().Next(1, 6);
        EmbedBuilder eb = new();
        ComponentBuilder cb = new();
        AddHome(cb);
        bool hasWon = winningNumber == number;

        await WriteParticipants(Context.Interaction.User.Id, hasWon).ConfigureAwait(false);

        if (hasWon)
        {
            eb.WithColor(Color.Gold);
            eb.WithTitle("Congratulations you are winner!");
            eb.WithDescription("You, by pure accident and sheer luck, picked the right number and have won yourself a lifetime Vanity ID role on this server!"
                + Environment.NewLine + Environment.NewLine
                + "The role will remain as long as you remain on this server, if you happen to leave it you will not get the role back."
                + Environment.NewLine + Environment.NewLine
                + "Head over to Home and to the Vanity IDs section to set it up for your account!"
                + Environment.NewLine + Environment.NewLine
                + "Once again, thank you for participating and have a great day.");

            var user = await Context.Guild.GetUserAsync(Context.User.Id).ConfigureAwait(false);
            await user.AddRoleAsync(_mareServicesConfiguration.GetValue<ulong?>(nameof(ServicesConfiguration.DiscordRoleAprilFools2024)).Value).ConfigureAwait(false);
        }
        else
        {
            eb.WithColor(Color.Red);
            eb.WithTitle("Fortune did not bless you");
            eb.WithDescription("You, through sheer misfortune, sadly did not pick the right number. (The winning number was " + winningNumber + ")"
                + Environment.NewLine + Environment.NewLine
                + "Better luck next time!"
                + Environment.NewLine + Environment.NewLine
                + "Once again, thank you for participating and regardless, have a great day.");
        }

        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    private async Task<Dictionary<ulong, bool>> GetParticipants()
    {
        await _fileSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            if (!File.Exists(_persistentFileName))
            {
                return new();
            }

            var json = await File.ReadAllTextAsync(_persistentFileName).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Dictionary<ulong, bool>>(json);
        }
        catch
        {
            return new();
        }
        finally
        {
            _fileSemaphore.Release();
        }
    }

    private async Task WriteParticipants(ulong participant, bool win)
    {
        await _fileSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            Dictionary<ulong, bool> participants = new();
            if (File.Exists(_persistentFileName))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_persistentFileName).ConfigureAwait(false);
                    participants = JsonSerializer.Deserialize<Dictionary<ulong, bool>>(json);
                }
                catch
                {
                    // probably empty file just deal with it
                }
            }

            participants[participant] = win;

            await File.WriteAllTextAsync(_persistentFileName, JsonSerializer.Serialize(participants)).ConfigureAwait(false);
        }
        finally
        {
            _fileSemaphore.Release();
        }
    }
}