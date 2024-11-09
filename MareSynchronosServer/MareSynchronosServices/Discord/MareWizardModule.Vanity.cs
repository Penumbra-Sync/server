using Discord.Interactions;
using Discord;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using System.Text;

namespace MareSynchronosServices.Discord;

public partial class MareWizardModule
{
    [ComponentInteraction("wizard-vanity")]
    public async Task ComponentVanity()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentVanity), Context.Interaction.User.Id);

        StringBuilder sb = new();
        var user = await Context.Guild.GetUserAsync(Context.User.Id).ConfigureAwait(false);
        bool userIsInVanityRole = _botServices.VanityRoles.Keys.Any(u => user.RoleIds.Contains(u.Id)) || !_botServices.VanityRoles.Any();
        if (!userIsInVanityRole)
        {
            sb.AppendLine("To be able to set Vanity IDs you must have one of the following roles:");
            foreach (var role in _botServices.VanityRoles)
            {
                sb.Append("- ").Append(role.Key.Mention).Append(" (").Append(role.Value).AppendLine(")");
            }
        }
        else
        {
            sb.AppendLine("Your current roles on this server allow you to set Vanity IDs.");
        }

        EmbedBuilder eb = new();
        eb.WithTitle("Vanity IDs");
        eb.WithDescription("You are able to set your Vanity IDs here." + Environment.NewLine
            + "Vanity IDs are a way to customize your displayed UID or Syncshell ID to others." + Environment.NewLine + Environment.NewLine
            + sb.ToString());
        eb.WithColor(Color.Blue);
        ComponentBuilder cb = new();
        AddHome(cb);
        if (userIsInVanityRole)
        {
            using var db = await GetDbContext().ConfigureAwait(false);
            await AddUserSelection(db, cb, "wizard-vanity-uid").ConfigureAwait(false);
            await AddGroupSelection(db, cb, "wizard-vanity-gid").ConfigureAwait(false);
        }

        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-vanity-uid")]
    public async Task SelectionVanityUid(string uid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}", nameof(SelectionVanityUid), Context.Interaction.User.Id, uid);

        using var db = await GetDbContext().ConfigureAwait(false);
        var user = db.Users.Single(u => u.UID == uid);
        EmbedBuilder eb = new();
        eb.WithColor(Color.Purple);
        eb.WithTitle($"Set Vanity UID for {uid}");
        eb.WithDescription($"You are about to change the Vanity UID for {uid}" + Environment.NewLine + Environment.NewLine
            + "The current Vanity UID is set to: **" + (user.Alias == null ? "No Vanity UID set" : user.Alias) + "**");
        ComponentBuilder cb = new();
        cb.WithButton("Cancel", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
        cb.WithButton("Set Vanity ID", "wizard-vanity-uid-set:" + uid, ButtonStyle.Primary, new Emoji("💅"));

        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-vanity-uid-set:*")]
    public async Task SelectionVanityUidSet(string uid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}", nameof(SelectionVanityUidSet), Context.Interaction.User.Id, uid);

        await RespondWithModalAsync<VanityUidModal>("wizard-vanity-uid-modal:" + uid).ConfigureAwait(false);
    }

    [ModalInteraction("wizard-vanity-uid-modal:*")]
    public async Task ConfirmVanityUidModal(string uid, VanityUidModal modal)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}:{vanity}", nameof(ConfirmVanityUidModal), Context.Interaction.User.Id, uid, modal.DesiredVanityUID);

        EmbedBuilder eb = new();
        ComponentBuilder cb = new();
        var desiredVanityUid = modal.DesiredVanityUID;
        using var db = await GetDbContext().ConfigureAwait(false);
        bool canAddVanityId = !db.Users.Any(u => u.UID == modal.DesiredVanityUID || u.Alias == modal.DesiredVanityUID);

        Regex rgx = new(@"^[_\-a-zA-Z0-9]{5,15}$", RegexOptions.ECMAScript);
        if (!rgx.Match(desiredVanityUid).Success)
        {
            eb.WithColor(Color.Red);
            eb.WithTitle("Invalid Vanity UID");
            eb.WithDescription("A Vanity UID must be between 5 and 15 characters long and only contain the letters A-Z, numbers 0-9, dashes (-) and underscores (_).");
            cb.WithButton("Cancel", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
            cb.WithButton("Pick Different UID", "wizard-vanity-uid-set:" + uid, ButtonStyle.Primary, new Emoji("💅"));
        }
        else if (!canAddVanityId)
        {
            eb.WithColor(Color.Red);
            eb.WithTitle("Vanity UID already taken");
            eb.WithDescription($"The Vanity UID {desiredVanityUid} has already been claimed. Please pick a different one.");
            cb.WithButton("Cancel", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
            cb.WithButton("Pick Different UID", "wizard-vanity-uid-set:" + uid, ButtonStyle.Primary, new Emoji("💅"));
        }
        else
        {
            var user = await db.Users.SingleAsync(u => u.UID == uid).ConfigureAwait(false);
            user.Alias = desiredVanityUid;
            db.Update(user);
            await db.SaveChangesAsync().ConfigureAwait(false);
            eb.WithColor(Color.Green);
            eb.WithTitle("Vanity UID successfully set");
            eb.WithDescription($"Your Vanity UID for \"{uid}\" was successfully changed to \"{desiredVanityUid}\"." + Environment.NewLine + Environment.NewLine
                + "For changes to take effect you need to reconnect to the Mare service.");
            await _botServices.LogToChannel($"{Context.User.Mention} VANITY UID SET: UID: {user.UID}, Vanity: {desiredVanityUid}").ConfigureAwait(false);
            AddHome(cb);
        }

        await ModifyModalInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-vanity-gid")]
    public async Task SelectionVanityGid(string gid)
    {
        _logger.LogInformation("{method}:{userId}:{uid}", nameof(SelectionVanityGid), Context.Interaction.User.Id, gid);

        using var db = await GetDbContext().ConfigureAwait(false);
        var group = db.Groups.Single(u => u.GID == gid);
        EmbedBuilder eb = new();
        eb.WithColor(Color.Purple);
        eb.WithTitle($"Set Vanity GID for {gid}");
        eb.WithDescription($"You are about to change the Vanity Syncshell ID for {gid}" + Environment.NewLine + Environment.NewLine
            + "The current Vanity Syncshell ID is set to: **" + (group.Alias == null ? "No Vanity Syncshell ID set" : group.Alias) + "**");
        ComponentBuilder cb = new();
        cb.WithButton("Cancel", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
        cb.WithButton("Set Vanity ID", "wizard-vanity-gid-set:" + gid, ButtonStyle.Primary, new Emoji("💅"));

        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-vanity-gid-set:*")]
    public async Task SelectionVanityGidSet(string gid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{gid}", nameof(SelectionVanityGidSet), Context.Interaction.User.Id, gid);

        await RespondWithModalAsync<VanityGidModal>("wizard-vanity-gid-modal:" + gid).ConfigureAwait(false);
    }

    [ModalInteraction("wizard-vanity-gid-modal:*")]
    public async Task ConfirmVanityGidModal(string gid, VanityGidModal modal)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{gid}:{vanity}", nameof(ConfirmVanityGidModal), Context.Interaction.User.Id, gid, modal.DesiredVanityGID);

        EmbedBuilder eb = new();
        ComponentBuilder cb = new();
        var desiredVanityGid = modal.DesiredVanityGID;
        using var db = await GetDbContext().ConfigureAwait(false);
        bool canAddVanityId = !db.Groups.Any(u => u.GID == modal.DesiredVanityGID || u.Alias == modal.DesiredVanityGID);

        Regex rgx = new(@"^[_\-a-zA-Z0-9]{5,20}$", RegexOptions.ECMAScript);
        if (!rgx.Match(desiredVanityGid).Success)
        {
            eb.WithColor(Color.Red);
            eb.WithTitle("Invalid Vanity Syncshell ID");
            eb.WithDescription("A Vanity Syncshell ID must be between 5 and 20 characters long and only contain the letters A-Z, numbers 0-9, dashes (-) and underscores (_).");
            cb.WithButton("Cancel", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
            cb.WithButton("Pick Different ID", "wizard-vanity-gid-set:" + gid, ButtonStyle.Primary, new Emoji("💅"));
        }
        else if (!canAddVanityId)
        {
            eb.WithColor(Color.Red);
            eb.WithTitle("Vanity Syncshell ID already taken");
            eb.WithDescription($"The Vanity Synshell ID \"{desiredVanityGid}\" has already been claimed. Please pick a different one.");
            cb.WithButton("Cancel", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
            cb.WithButton("Pick Different ID", "wizard-vanity-gid-set:" + gid, ButtonStyle.Primary, new Emoji("💅"));
        }
        else
        {
            var group = await db.Groups.SingleAsync(u => u.GID == gid).ConfigureAwait(false);
            group.Alias = desiredVanityGid;
            db.Update(group);
            await db.SaveChangesAsync().ConfigureAwait(false);
            eb.WithColor(Color.Green);
            eb.WithTitle("Vanity Syncshell ID successfully set");
            eb.WithDescription($"Your Vanity Syncshell ID for {gid} was successfully changed to \"{desiredVanityGid}\"." + Environment.NewLine + Environment.NewLine
                + "For changes to take effect you need to reconnect to the Mare service.");
            AddHome(cb);
            await _botServices.LogToChannel($"{Context.User.Mention} VANITY GID SET: GID: {group.GID}, Vanity: {desiredVanityGid}").ConfigureAwait(false);
        }

        await ModifyModalInteraction(eb, cb).ConfigureAwait(false);
    }
}
