using Discord.Interactions;
using Discord;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Utils.Configuration;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServices.Discord;

public partial class MareWizardModule
{
    [ComponentInteraction("wizard-delete")]
    public async Task ComponentDelete()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentDelete), Context.Interaction.User.Id);

        using var mareDb = await GetDbContext().ConfigureAwait(false);
        EmbedBuilder eb = new();
        eb.WithTitle("Delete Account");
        eb.WithDescription("You can delete your primary or secondary UIDs here." + Environment.NewLine + Environment.NewLine
            + "__Note: deleting your primary UID will delete all associated secondary UIDs as well.__" + Environment.NewLine + Environment.NewLine
            + "- 1️⃣ is your primary account/UID" + Environment.NewLine
            + "- 2️⃣ are all your secondary accounts/UIDs" + Environment.NewLine
            + "If you are using Vanity UIDs the original UID is displayed in the second line of the account selection.");
        eb.WithColor(Color.Blue);

        ComponentBuilder cb = new();
        await AddUserSelection(mareDb, cb, "wizard-delete-select").ConfigureAwait(false);
        AddHome(cb);
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-delete-select")]
    public async Task SelectionDeleteAccount(string uid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}", nameof(SelectionDeleteAccount), Context.Interaction.User.Id, uid);

        using var mareDb = await GetDbContext().ConfigureAwait(false);
        bool isPrimary = mareDb.Auth.Single(u => u.UserUID == uid).PrimaryUserUID == null;
        EmbedBuilder eb = new();
        eb.WithTitle($"Are you sure you want to delete {uid}?");
        eb.WithDescription($"This operation is irreversible. All your pairs, joined syncshells and information stored on the service for {uid} will be " +
            $"irrevocably deleted." +
            (isPrimary ? (Environment.NewLine + Environment.NewLine +
            "⚠️ **You are about to delete a Primary UID, all attached Secondary UIDs and their information will be deleted as well.** ⚠️") : string.Empty));
        eb.WithColor(Color.Purple);
        ComponentBuilder cb = new();
        cb.WithButton("Cancel", "wizard-delete", emote: new Emoji("❌"));
        cb.WithButton($"Delete {uid}", "wizard-delete-confirm:" + uid, ButtonStyle.Danger, emote: new Emoji("🗑️"));
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-delete-confirm:*")]
    public async Task ComponentDeleteAccountConfirm(string uid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}", nameof(ComponentDeleteAccountConfirm), Context.Interaction.User.Id, uid);

        await RespondWithModalAsync<ConfirmDeletionModal>("wizard-delete-confirm-modal:" + uid).ConfigureAwait(false);
    }

    [ModalInteraction("wizard-delete-confirm-modal:*")]
    public async Task ModalDeleteAccountConfirm(string uid, ConfirmDeletionModal modal)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}", nameof(ModalDeleteAccountConfirm), Context.Interaction.User.Id, uid);

        try
        {
            if (!string.Equals("DELETE", modal.Delete, StringComparison.Ordinal))
            {
                EmbedBuilder eb = new();
                eb.WithTitle("Did not confirm properly");
                eb.WithDescription($"You entered {modal.Delete} but requested was DELETE. Please try again and enter DELETE to confirm.");
                eb.WithColor(Color.Red);
                ComponentBuilder cb = new();
                cb.WithButton("Cancel", "wizard-delete", emote: new Emoji("❌"));
                cb.WithButton("Retry", "wizard-delete-confirm:" + uid, emote: new Emoji("🔁"));

                await ModifyModalInteraction(eb, cb).ConfigureAwait(false);
            }
            else
            {
                var maxGroupsByUser = _mareClientConfigurationService.GetValueOrDefault(nameof(ServerConfiguration.MaxGroupUserCount), 3);

                using var db = await GetDbContext().ConfigureAwait(false);
                var user = await db.Users.SingleAsync(u => u.UID == uid).ConfigureAwait(false);
                var lodestone = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.User.UID == uid).ConfigureAwait(false);
                await SharedDbFunctions.PurgeUser(_logger, user, db, maxGroupsByUser).ConfigureAwait(false);

                EmbedBuilder eb = new();
                eb.WithTitle($"Account {uid} successfully deleted");
                eb.WithColor(Color.Green);
                ComponentBuilder cb = new();
                AddHome(cb);

                await ModifyModalInteraction(eb, cb).ConfigureAwait(false);

                await _botServices.LogToChannel($"{Context.User.Mention} DELETE SUCCESS: {uid}").ConfigureAwait(false);

                // only remove role if deleted uid has lodestone attached (== primary uid)
                if (lodestone != null)
                {
                    await _botServices.RemoveRegisteredRoleAsync(Context.Interaction.User).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling modal delete account confirm");
        }
    }
}
