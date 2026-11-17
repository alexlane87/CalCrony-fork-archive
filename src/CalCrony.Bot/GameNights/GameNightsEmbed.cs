using System.Text;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Discord;

namespace CalCrony.Bot.GameNights;

/// <summary>What the Game Nights extension adds to an event's Discord post: a short summary of the night's
/// game answers, and a Pick games button. The core builder calls three small hooks here; with no summary on the
/// event (the server has no games) every hook does nothing and the post is exactly what it always was.</summary>
public static class GameNightsEmbed
{
    /// <summary>The embed field's name.</summary>
    public const string FieldName = "🎲 Game night";

    /// <summary>Custom-id prefix of the Pick games button: <c>gnpick:{eventId}</c>.</summary>
    public const string PickPrefix = "gnpick";

    /// <summary>How much of the embed's size budget the summary may use (name, value and Discord's own slack).</summary>
    public const int FieldBudget = 700;

    private const int ValueLimit = 600;
    private const int ButtonsPerRow = 5;
    private const int MaxRows = 5;
    private const int GamesShown = 5;

    /// <summary>The size to hold back from the rest of the embed for the summary field.</summary>
    /// <param name="ev">The event.</param>
    /// <returns>Zero when there is no summary.</returns>
    public static int Reserve(EventDto ev) => ev.GameNights is null ? 0 : FieldBudget;

    /// <summary>Adds the summary field to the embed.</summary>
    /// <param name="builder">The embed being built.</param>
    /// <param name="ev">The event.</param>
    public static void AddSummary(EmbedBuilder builder, EventDto ev)
    {
        if (ev.GameNights is { } summary)
        {
            builder.AddField(FieldName, SummaryText(summary), inline: false);
        }
    }

    /// <summary>Adds the Pick games button after the RSVP buttons: on the last row if it has room, else on a
    /// new row, and nowhere if Discord's five rows are already used.</summary>
    /// <param name="builder">The components being built.</param>
    /// <param name="row">The row the RSVP buttons are still being added to (not yet in the builder).</param>
    /// <param name="ev">The event.</param>
    public static void TryAddPickGames(ComponentBuilder builder, ref ActionRowBuilder row, EventDto ev)
    {
        if (ev.GameNights is null)
        {
            return;
        }

        if (row.Components.Count >= ButtonsPerRow)
        {
            if ((builder.ActionRows?.Count ?? 0) + 2 > MaxRows)
            {
                return;
            }

            builder.AddRow(row);
            row = new ActionRowBuilder();
        }

        row.WithButton("Pick games", $"{PickPrefix}:{ev.Id}", ButtonStyle.Secondary, new Emoji("🎲"));
    }

    /// <summary>The summary's text: how many have answered and what they are, then the busiest games with a warning
    /// on any that people want and nobody can teach. Always under <see cref="ValueLimit"/> characters.</summary>
    /// <param name="summary">The night's summary.</param>
    /// <returns>The field value.</returns>
    public static string SummaryText(NightSignalsSummary summary)
    {
        if (summary.Respondents == 0)
        {
            return "Nobody has picked games yet. Press **Pick games** to say what you'd like to play, teach or learn.";
        }

        var text = new StringBuilder();
        text.Append($"**{summary.Respondents}** answered · 🧑‍🏫 {summary.Teachers} teaching · 🎯 {summary.Experienced} know their games · 🌱 {summary.Fresh} new to something");

        var wanted = summary.Games.Where(g => g.Interested > 0).ToList();
        foreach (var game in wanted.Take(GamesShown))
        {
            var line = $"\n**{Trim(game.GameName, 40)}**: {game.Interested} keen" +
                       (game.CanTeach > 0 ? $" · {game.CanTeach} can teach" : " · ⚠️ no teacher yet");
            if (text.Length + line.Length > ValueLimit - 20)
            {
                break;
            }

            text.Append(line);
        }

        var hidden = wanted.Count - Math.Min(wanted.Count, GamesShown);
        if (hidden > 0 && text.Length + 20 <= ValueLimit)
        {
            text.Append($"\n+{hidden} more");
        }

        return text.Length <= ValueLimit ? text.ToString() : text.ToString(0, ValueLimit - 1) + "…";
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
