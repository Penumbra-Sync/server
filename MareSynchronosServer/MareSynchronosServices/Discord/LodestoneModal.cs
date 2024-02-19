using Discord;
using Discord.Interactions;

namespace MareSynchronosServices.Discord;

// todo: remove all this crap at some point

public class LodestoneModal : IModal
{
    public string Title => "Verify with Lodestone";

    [InputLabel("Enter the Lodestone URL of your Character")]
    [ModalTextInput("lodestone_url", TextInputStyle.Short, "https://*.finalfantasyxiv.com/lodestone/character/<CHARACTERID>/")]
    public string LodestoneUrl { get; set; }
}
