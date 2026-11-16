using CalCrony.Contracts.GameNights;
using Discord;

namespace CalCrony.Bot.GameNights;

/// <summary>The messages of the /game add steps. Pure builders, so the custom ids and option lists are pinned
/// by tests. Discord limits (25 options per select, 100 characters per custom id) are respected by
/// construction: every select here has two to three options and every id is a prefix plus one guid.</summary>
public static class GameWizardView
{
    /// <summary>Custom-id prefixes, one per step.</summary>
    public const string TypePrefix = "gnadd:type";
    public const string TemplatePrefix = "gnadd:tmpl";
    public const string RolePrefix = "gnadd:role";
    public const string NewRolePrefix = "gnadd:newrole";
    public const string NoRolePrefix = "gnadd:norole";
    public const string PanelPrefix = "gnpanel";
    public const string FollowPrefix = "gnfollow";

    /// <summary>The "how is a result recorded" step.</summary>
    /// <param name="draft">The draft so far.</param>
    /// <returns>The message text and its select menu.</returns>
    public static (string Text, MessageComponent Components) ResultTypeStep(GameDraft draft) => (
        $"**{draft.Name}**, {draft.MinSize} to {draft.MaxSize} players.\n**Step 2 of 4:** how is a result recorded?",
        new ComponentBuilder().WithSelectMenu(new SelectMenuBuilder()
            .WithCustomId($"{TypePrefix}:{draft.Id}")
            .WithPlaceholder("Choose how a result is recorded")
            .AddOption("Finishing places", nameof(GameResultType.RankedPlacement), "Everyone gets a place; ties allowed")
            .AddOption("A single winner", nameof(GameResultType.SingleWinner), "Only the winner is recorded")
            .AddOption("A winning side", nameof(GameResultType.WinningSide), "Players are on two sides; one wins")).Build());

    /// <summary>The "which questions to start with" step.</summary>
    /// <param name="draft">The draft so far.</param>
    /// <returns>The message text and its select menu.</returns>
    public static (string Text, MessageComponent Components) TemplateStep(GameDraft draft) => (
        "**Step 3 of 4:** what should a result report ask? You can add more questions later on the website.",
        new ComponentBuilder().WithSelectMenu(new SelectMenuBuilder()
            .WithCustomId($"{TemplatePrefix}:{draft.Id}")
            .WithPlaceholder("Choose the starting questions")
            .AddOption("Players and final scores", nameof(GameWizard.FieldTemplate.PlayersAndScores), "Each player's score is required")
            .AddOption("Players only", nameof(GameWizard.FieldTemplate.PlayersOnly), "Just who played and how they placed")).Build());

    /// <summary>The role step: attach an existing role (a role select menu), make a new one, or go without.
    /// A warning is shown first when the bot cannot make roles.</summary>
    /// <param name="draft">The draft so far.</param>
    /// <param name="createWarning">What stops the bot making a role, or null when it can.</param>
    /// <param name="notice">An extra line (for example why the role just picked was refused).</param>
    /// <returns>The message text and its components.</returns>
    public static (string Text, MessageComponent Components) RoleStep(GameDraft draft, string? createWarning, string? notice = null)
    {
        var text = "**Step 4 of 4:** members follow a game by taking its role. Pick an existing role, or make a new one." +
                   (createWarning is null ? "" : $"\n⚠️ {createWarning}") +
                   (notice is null ? "" : $"\n⚠️ {notice}");
        var components = new ComponentBuilder()
            .WithSelectMenu(new SelectMenuBuilder()
                .WithCustomId($"{RolePrefix}:{draft.Id}")
                .WithType(ComponentType.RoleSelect)
                .WithPlaceholder("Use an existing role")
                .WithMinValues(1).WithMaxValues(1), row: 0)
            .WithButton($"Create a “{Trim(draft.Name, 50)}” role", $"{NewRolePrefix}:{draft.Id}", ButtonStyle.Primary, row: 1)
            .WithButton("No role", $"{NoRolePrefix}:{draft.Id}", ButtonStyle.Secondary, row: 1)
            .Build();
        return (text, components);
    }

    /// <summary>The message after the game is created.</summary>
    /// <param name="game">The created game.</param>
    /// <param name="roleMention">How the role is shown ("@Chess"), or null when there is none.</param>
    /// <returns>The text and, when the game has a role, a button that posts a public Follow panel.</returns>
    public static (string Text, MessageComponent? Components) Created(GameDto game, string? roleMention) => (
        $"✅ **{game.Spec.Name}** is in the library ({game.Spec.MinSize}–{game.Spec.MaxSize} players)." +
        (roleMention is null ? "\nIt has no follow role." : $"\nMembers follow it by taking {roleMention}."),
        roleMention is null ? null : new ComponentBuilder()
            .WithButton("Post a Follow button here", $"{PanelPrefix}:{game.Id}", ButtonStyle.Success).Build());

    /// <summary>The public message members press to follow a game.</summary>
    /// <param name="game">The game (with a role).</param>
    /// <returns>The embed and its Follow button.</returns>
    public static (Embed Embed, MessageComponent Components) FollowPanel(GameDto game) => (
        new EmbedBuilder()
            .WithTitle($"🎲 {game.Spec.Name}")
            .WithDescription("Press the button to follow this game: you'll get its role, so people can find you when a table forms. Press again to stop.")
            .Build(),
        new ComponentBuilder().WithButton("Follow / unfollow", $"{FollowPrefix}:{game.Id}", ButtonStyle.Primary, new Emoji("🔔")).Build());

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
