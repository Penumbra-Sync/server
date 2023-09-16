using Discord.Interactions;
using Discord;
using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Models;

namespace MareSynchronosServices.Discord;

public partial class MareWizardModule
{
    [ComponentInteraction("wizard-register")]
    public async Task ComponentRegister()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        EmbedBuilder eb = new();
        eb.WithColor(Color.Blue);
        eb.WithTitle("Start Registration");
        eb.WithDescription("Here you can start the registration process with the Mare Synchronos server of this Discord." + Environment.NewLine + Environment.NewLine
            + "- Have your Lodestone URL ready (i.e. https://eu.finalfantasyxiv.com/lodestone/character/XXXXXXXXX)" + Environment.NewLine
            + "  - The registration requires you to modify your Lodestone profile with a generated code for verification" + Environment.NewLine
            + "  - You need to have a paid FFXIV account or someone who can assist you with registration if you can't edit your own Lodestone" + Environment.NewLine
            + "- Do not use this on mobile because you will need to be able to copy the generated secret key" + Environment.NewLine);
        ComponentBuilder cb = new();
        AddHome(cb);
        cb.WithButton("Start Registration", "wizard-register-start", ButtonStyle.Primary, emote: new Emoji("🌒"));
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-register-start")]
    public async Task ComponentRegisterStart()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        using var db = GetDbContext();
        var entry = await db.LodeStoneAuth.SingleOrDefaultAsync(u => u.DiscordId == Context.User.Id && u.StartedAt != null).ConfigureAwait(false);
        if (entry != null)
        {
            db.LodeStoneAuth.Remove(entry);
        }
        _botServices.DiscordLodestoneMapping.TryRemove(Context.User.Id, out _);
        _botServices.DiscordVerifiedUsers.TryRemove(Context.User.Id, out _);

        await db.SaveChangesAsync().ConfigureAwait(false);

        await RespondWithModalAsync<LodestoneModal>("wizard-register-lodestone-modal").ConfigureAwait(false);
    }

    [ModalInteraction("wizard-register-lodestone-modal")]
    public async Task ModalRegister(LodestoneModal lodestoneModal)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        EmbedBuilder eb = new();
        eb.WithColor(Color.Purple);
        var success = await HandleRegisterModalAsync(eb, lodestoneModal).ConfigureAwait(false);
        ComponentBuilder cb = new();
        cb.WithButton("Cancel", "wizard-register", ButtonStyle.Secondary, emote: new Emoji("❌"));
        if (success.Item1) cb.WithButton("Verify", "wizard-register-verify:" + success.Item2, ButtonStyle.Primary, emote: new Emoji("✅"));
        else cb.WithButton("Try again", "wizard-register-start", ButtonStyle.Primary, emote: new Emoji("🔁"));
        await ModifyModalInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-register-verify:*")]
    public async Task ComponentRegisterVerify(string verificationCode)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _botServices.VerificationQueue.Enqueue(new KeyValuePair<ulong, Action<IServiceProvider>>(Context.User.Id,
            async (_) => await HandleVerifyAsync(Context.User.Id, verificationCode).ConfigureAwait(false)));
        EmbedBuilder eb = new();
        ComponentBuilder cb = new();
        eb.WithColor(Color.Purple);
        cb.WithButton("Cancel", "wizard-register", ButtonStyle.Secondary, emote: new Emoji("❌"));
        cb.WithButton("Check", "wizard-register-verify-check:" + verificationCode, ButtonStyle.Primary, emote: new Emoji("❓"));
        eb.WithTitle("Verification Pending");
        eb.WithDescription("Please wait until the bot verifies your registration." + Environment.NewLine
            + "Press \"Check\" to check if the verification has been already processed" + Environment.NewLine + Environment.NewLine
            + "__This will not advance automatically, you need to press \"Check\".__");
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-register-verify-check:*")]
    public async Task ComponentRegisterVerifyCheck(string verificationCode)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        EmbedBuilder eb = new();
        ComponentBuilder cb = new();
        bool stillEnqueued = _botServices.VerificationQueue.Any(k => k.Key == Context.User.Id);
        bool verificationRan = _botServices.DiscordVerifiedUsers.TryGetValue(Context.User.Id, out bool verified);
        if (!verificationRan)
        {
            if (stillEnqueued)
            {
                eb.WithColor(Color.Gold);
                eb.WithTitle("Your verification is still pending");
                eb.WithDescription("Please try again and click Check in a few seconds");
                cb.WithButton("Cancel", "wizard-register", ButtonStyle.Secondary, emote: new Emoji("❌"));
                cb.WithButton("Check", "wizard-register-verify-check:" + verificationCode, ButtonStyle.Primary, emote: new Emoji("❓"));
            }
            else
            {
                eb.WithColor(Color.Red);
                eb.WithTitle("Something went wrong");
                eb.WithDescription("Your verification was processed but did not arrive properly. Please try to start the registration from the start.");
                cb.WithButton("Restart", "wizard-register", ButtonStyle.Primary, emote: new Emoji("🔁"));
            }
        }
        else
        {
            if (verified)
            {
                eb.WithColor(Color.Green);
                using var db = _services.CreateScope().ServiceProvider.GetRequiredService<MareDbContext>();
                var (uid, key) = await HandleAddUser(db).ConfigureAwait(false);
                eb.WithTitle($"Registration successful, your UID: {uid}");
                eb.WithDescription("This is your private secret key. Do not share this private secret key with anyone. **If you lose it, it is irrevocably lost.**"
                                             + Environment.NewLine + Environment.NewLine
                                             + $"**{key}**"
                                             + Environment.NewLine + Environment.NewLine
                                             + "Enter this key in Mare Synchronos and hit save to connect to the service."
                                             + Environment.NewLine
                                             + "You should connect as soon as possible to not get caught by the automatic cleanup process."
                                             + Environment.NewLine
                                             + "Have fun.");
                AddHome(cb);
            }
            else
            {
                eb.WithColor(Color.Gold);
                eb.WithTitle("Failed to verify registration");
                eb.WithDescription("The bot was not able to find the required verification code on your Lodestone profile." + Environment.NewLine + Environment.NewLine
                    + "Please restart your verification process, make sure to save your profile _twice_ for it to be properly saved." + Environment.NewLine + Environment.NewLine
                    + "The code the bot is looking for is" + Environment.NewLine + Environment.NewLine
                    + "**" + verificationCode + "**");
                cb.WithButton("Cancel", "wizard-register", emote: new Emoji("❌"));
                cb.WithButton("Retry", "wizard-register-verify:" + verificationCode, ButtonStyle.Primary, emote: new Emoji("🔁"));
            }
        }

        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    private async Task<(bool, string)> HandleRegisterModalAsync(EmbedBuilder embed, LodestoneModal arg)
    {
        var lodestoneId = ParseCharacterIdFromLodestoneUrl(arg.LodestoneUrl);
        if (lodestoneId == null)
        {
            embed.WithTitle("Invalid Lodestone URL");
            embed.WithDescription("The lodestone URL was not valid. It should have following format:" + Environment.NewLine
                + "https://eu.finalfantasyxiv.com/lodestone/character/YOUR_LODESTONE_ID/");
            return (false, string.Empty);
        }

        // check if userid is already in db
        using var scope = _services.CreateScope();

        var hashedLodestoneId = StringUtils.Sha256String(lodestoneId.ToString());

        using var db = scope.ServiceProvider.GetService<MareDbContext>();

        // check if discord id or lodestone id is banned
        if (db.BannedRegistrations.Any(a => a.DiscordIdOrLodestoneAuth == hashedLodestoneId))
        {
            embed.WithDescription("This account is banned");
            return (false, string.Empty);
        }

        if (db.LodeStoneAuth.Any(a => a.HashedLodestoneId == hashedLodestoneId))
        {
            // character already in db
            embed.WithDescription("This lodestone character already exists in the Database. If you want to attach this character to your current Discord account use relink.");
            return (false, string.Empty);
        }

        string lodestoneAuth = await GenerateLodestoneAuth(Context.User.Id, hashedLodestoneId, db).ConfigureAwait(false);
        // check if lodestone id is already in db
        embed.WithTitle("Authorize your character");
        embed.WithDescription("Add following key to your character profile at https://na.finalfantasyxiv.com/lodestone/my/setting/profile/"
                              + Environment.NewLine + Environment.NewLine
                              + $"**{lodestoneAuth}**"
                              + Environment.NewLine + Environment.NewLine
                              + $"**! THIS IS NOT THE KEY YOU HAVE TO ENTER IN MARE !**"
                              + Environment.NewLine + Environment.NewLine
                              + "Once added and saved, use the button below to Verify and finish registration and receive a secret key to use for Mare Synchronos."
                              + Environment.NewLine
                              + "__You can delete the entry from your profile after verification.__"
                              + Environment.NewLine + Environment.NewLine
                              + "The verification will expire in approximately 15 minutes. If you fail to verify the registration will be invalidated and you have to register again.");
        _botServices.DiscordLodestoneMapping[Context.User.Id] = lodestoneId.ToString();

        return (true, lodestoneAuth);
    }

    private async Task HandleVerifyAsync(ulong userid, string authString)
    {
        var req = new HttpClient();

        _botServices.DiscordVerifiedUsers.Remove(userid, out _);
        if (_botServices.DiscordLodestoneMapping.ContainsKey(userid))
        {
            var randomServer = _botServices.LodestoneServers[random.Next(_botServices.LodestoneServers.Length)];
            var response = await req.GetAsync($"https://{randomServer}.finalfantasyxiv.com/lodestone/character/{_botServices.DiscordLodestoneMapping[userid]}").ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (content.Contains(authString))
                {
                    _botServices.DiscordVerifiedUsers[userid] = true;
                    _botServices.DiscordLodestoneMapping.TryRemove(userid, out _);
                }
                else
                {
                    _botServices.DiscordVerifiedUsers[userid] = false;
                }
            }
        }
    }

    private async Task<(string, string)> HandleAddUser(MareDbContext db)
    {
        var lodestoneAuth = db.LodeStoneAuth.SingleOrDefault(u => u.DiscordId == Context.User.Id);

        var user = new User();

        var hasValidUid = false;
        while (!hasValidUid)
        {
            var uid = StringUtils.GenerateRandomString(10);
            if (db.Users.Any(u => u.UID == uid || u.Alias == uid)) continue;
            user.UID = uid;
            hasValidUid = true;
        }

        // make the first registered user on the service to admin
        if (!await db.Users.AnyAsync().ConfigureAwait(false))
        {
            user.IsAdmin = true;
        }

        user.LastLoggedIn = DateTime.UtcNow;

        var computedHash = StringUtils.Sha256String(StringUtils.GenerateRandomString(64) + DateTime.UtcNow.ToString());
        var auth = new Auth()
        {
            HashedKey = StringUtils.Sha256String(computedHash),
            User = user,
        };

        await db.Users.AddAsync(user).ConfigureAwait(false);
        await db.Auth.AddAsync(auth).ConfigureAwait(false);

        _botServices.Logger.LogInformation("User registered: {userUID}", user.UID);

        lodestoneAuth.StartedAt = null;
        lodestoneAuth.User = user;
        lodestoneAuth.LodestoneAuthString = null;

        await db.SaveChangesAsync().ConfigureAwait(false);

        _botServices.DiscordVerifiedUsers.Remove(Context.User.Id, out _);

        return (user.UID, computedHash);
    }
}
