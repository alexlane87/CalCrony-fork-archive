using CalCrony.Contracts.GameNights;
using Discord;

namespace CalCrony.Bot.GameNights;

/// <summary>The private "Pick games" panel: a page of up to four games, each with its own select menu, plus
/// previous and next buttons. Each select saves as soon as it changes, so there is no Save button and nothing is
/// lost if someone walks away. Four games per page because Discord allows five rows and the buttons take one. Pure,
/// so the ids, labels and limits are pinned by tests.</summary>
public static class SignalPanel
{
    /// <summary>Custom-id prefix of a game's select: <c>gnsig:{eventId}:{gameId}</c>.</summary>
    public const string SelectPrefix = "gnsig";

    /// <summary>Custom-id prefix of the page buttons: <c>gnpage:{eventId}:{page}</c>.</summary>
    public const string PagePrefix = "gnpage";

    /// <summary>The select value that clears an answer.</summary>
    public const string ClearValue = "clear";

    /// <summary>Games on one page.</summary>
    public const int GamesPerPage = 4;

    /// <summary>The words for each answer.</summary>
    /// <param name="kind">The answer.</param>
    /// <returns>The label.</returns>
    public static string Describe(SignalKind kind) => kind switch
    {
        SignalKind.CanTeach => "I can teach it",
        SignalKind.CanPlay => "I can play it",
        SignalKind.NewToIt => "I'm new to it",
        _ => "Not tonight",
    };

    private static string Emoji(SignalKind kind) => kind switch
    {
        SignalKind.CanTeach => "🧑‍🏫",
        SignalKind.CanPlay => "🎯",
        SignalKind.NewToIt => "🌱",
        _ => "🚫",
    };

    /// <summary>How many pages the games need.</summary>
    /// <param name="gameCount">The number of games.</param>
    /// <returns>At least one.</returns>
    public static int PageCount(int gameCount) => Math.Max(1, (gameCount + GamesPerPage - 1) / GamesPerPage);

    /// <summary>The page a game sits on, given the games in the order the panel lists them.</summary>
    /// <param name="games">The games, in panel order.</param>
    /// <param name="gameId">The game.</param>
    /// <returns>The page number (from 0); 0 when the game is not listed.</returns>
    public static int PageOf(IReadOnlyList<GameDto> games, Guid gameId)
    {
        var index = games.ToList().FindIndex(g => g.Id == gameId);
        return index < 0 ? 0 : index / GamesPerPage;
    }

    /// <summary>Builds one page of the panel.</summary>
    /// <param name="eventId">The game night.</param>
    /// <param name="eventTitle">Its title.</param>
    /// <param name="games">Every game the member can answer about, in a stable order.</param>
    /// <param name="mine">The member's answers so far, by game.</param>
    /// <param name="page">The page to show (clamped into range).</param>
    /// <returns>The embed and its components.</returns>
    public static (Embed Embed, MessageComponent Components) Build(
        Guid eventId, string eventTitle, IReadOnlyList<GameDto> games, IReadOnlyDictionary<Guid, SignalKind> mine, int page)
    {
        var pages = PageCount(games.Count);
        page = Math.Clamp(page, 0, pages - 1);
        var shown = games.Skip(page * GamesPerPage).Take(GamesPerPage).ToList();

        var embed = new EmbedBuilder()
            .WithTitle($"Pick games: {Trim(eventTitle, 200)}")
            .WithDescription("For each game, say whether you can teach it, can play it, are new to it, or want to skip it tonight. " +
                             "Each choice saves straight away." + (pages > 1 ? $"\nPage {page + 1} of {pages}." : ""))
            .Build();

        var components = new ComponentBuilder();
        for (var i = 0; i < shown.Count; i++)
        {
            var game = shown[i];
            var name = Trim(game.Spec.Name, 40);
            mine.TryGetValue(game.Id, out var current);
            var has = mine.ContainsKey(game.Id);
            var select = new SelectMenuBuilder()
                .WithCustomId($"{SelectPrefix}:{eventId}:{game.Id}")
                .WithPlaceholder($"{name}: pick one");
            foreach (var kind in Enum.GetValues<SignalKind>())
            {
                select.AddOption($"{name}: {Describe(kind)}", kind.ToString(), emote: new Emoji(Emoji(kind)), isDefault: has && current == kind);
            }

            select.AddOption($"{name}: clear my answer", ClearValue, emote: new Emoji("↩️"));
            components.WithSelectMenu(select, row: i);
        }

        if (pages > 1)
        {
            components.WithButton("‹ Previous", $"{PagePrefix}:{eventId}:{page - 1}", ButtonStyle.Secondary, disabled: page == 0, row: 4);
            components.WithButton("Next ›", $"{PagePrefix}:{eventId}:{page + 1}", ButtonStyle.Secondary, disabled: page == pages - 1, row: 4);
        }

        return (embed, components.Build());
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
