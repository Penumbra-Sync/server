using Discord.Interactions;
using Discord;
using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;
using MareSynchronosShared.Models;
using MareSynchronosShared.Utils;

namespace MareSynchronosServices.Discord;

public partial class MareWizardModule
{
    [ComponentInteraction("wizard-secondary")]
    public async Task ComponentSecondary()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentSecondary), Context.Interaction.User.Id);

        using var mareDb = await GetDbContext().ConfigureAwait(false);
        var primaryUID = (await mareDb.LodeStoneAuth.Include(u => u.User).SingleAsync(u => u.DiscordId == Context.User.Id).ConfigureAwait(false)).User.UID;
        var secondaryUids = await mareDb.Auth.CountAsync(p => p.PrimaryUserUID == primaryUID).ConfigureAwait(false);
        EmbedBuilder eb = new();
        eb.WithColor(Color.Blue);
        eb.WithTitle("Secondary UID");
        eb.WithDescription("You can create secondary UIDs here. " + Environment.NewLine + Environment.NewLine
            + "Secondary UIDs act as completely separate Mare accounts with their own pair list, joined syncshells, UID and so on." + Environment.NewLine
            + "Use this to create UIDs if you want to use Mare on two separate game instances at once or keep your alts private." + Environment.NewLine + Environment.NewLine
            + "__Note:__ Creating a Secondary UID is _not_ necessary to use Mare for alts." + Environment.NewLine + Environment.NewLine
            + $"You currently have {secondaryUids} Secondary UIDs out of a maximum of 20.");
        ComponentBuilder cb = new();
        AddHome(cb);
        cb.WithButton("Create Secondary UID", "wizard-secondary-create:" + primaryUID, ButtonStyle.Primary, emote: new Emoji("2️⃣"), disabled: secondaryUids >= 20);
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-secondary-create:*")]
    public async Task ComponentSecondaryCreate(string primaryUid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{primary}", nameof(ComponentSecondaryCreate), Context.Interaction.User.Id, primaryUid);

        using var mareDb = await GetDbContext().ConfigureAwait(false);
        EmbedBuilder eb = new();
        eb.WithTitle("Secondary UID created");
        eb.WithColor(Color.Green);
        ComponentBuilder cb = new();
        AddHome(cb);
        await HandleAddSecondary(mareDb, eb, primaryUid).ConfigureAwait(false);
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    public async Task HandleAddSecondary(MareDbContext db, EmbedBuilder embed, string primaryUID)
    {
        User newUser = new()
        {
            IsAdmin = false,
            IsModerator = false,
            LastLoggedIn = DateTime.UtcNow,
        };

        var hasValidUid = false;
        while (!hasValidUid)
        {
            var uid = StringUtils.GenerateRandomString(10);
            if (await db.Users.AnyAsync(u => u.UID == uid || u.Alias == uid).ConfigureAwait(false)) continue;
            newUser.UID = uid;
            hasValidUid = true;
        }

        var computedHash = StringUtils.Sha256String(StringUtils.GenerateRandomString(64) + DateTime.UtcNow.ToString());
        var auth = new Auth()
        {
            HashedKey = StringUtils.Sha256String(computedHash),
            User = newUser,
            PrimaryUserUID = primaryUID
        };

        await db.Users.AddAsync(newUser).ConfigureAwait(false);
        await db.Auth.AddAsync(auth).ConfigureAwait(false);

        await db.SaveChangesAsync().ConfigureAwait(false);

        embed.WithDescription("A secondary UID for you was created, use the information below and add the secret key to the Mare setings in the Service Settings tab.");
        embed.AddField("UID", newUser.UID);
        embed.AddField("Secret Key", computedHash);

        await _botServices.LogToChannel($"{Context.User.Mention} SECONDARY SUCCESS: {newUser.UID}").ConfigureAwait(false);
    }

}
