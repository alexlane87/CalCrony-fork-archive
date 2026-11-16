using CalCrony.Contracts.GameNights;
using Discord;

namespace CalCrony.Bot.GameNights;

/// <summary>The words the bot puts in a table's Discord thread. Pure, so it is testable without Discord.
/// Members are mentioned by id and nothing else is ever pinged: the callers pin
/// <c>AllowedMentions</c> to exactly the people named here.</summary>
public static class TableText
{
    private const int ThreadNameLimit = 100;

    /// <summary>The thread's name: "Root, table 2: Game night", cut to Discord's 100 characters.</summary>
    /// <param name="table">The table.</param>
    /// <param name="eventTitle">The game night's title.</param>
    /// <returns>The name.</returns>
    public static string ThreadName(TableDto table, string eventTitle)
    {
        var name = $"{table.GameName}, table {table.Number}: {eventTitle}";
        return name.Length <= ThreadNameLimit ? name : name[..ThreadNameLimit];
    }

    /// <summary>The embed on the starter message in the channel (the thread hangs off it).</summary>
    /// <param name="table">The table.</param>
    /// <param name="eventTitle">The game night's title.</param>
    /// <returns>The embed.</returns>
    public static Embed Starter(TableDto table, string eventTitle) =>
        new EmbedBuilder()
            .WithTitle($"🎲 {table.GameName}, table {table.Number}")
            .WithDescription($"{eventTitle}\n{table.Seats.Count} of {table.Size} seats taken" +
                             (table.HostUserId is { } host ? $"\nHost: <@{host}>" : "\nNo host yet"))
            .WithFooter("Join with /table join, or leave with /table leave. The thread below is this table's chat.")
            .Build();

    /// <summary>The first message in the thread: it addresses the host, who is prompted before anyone else.</summary>
    /// <param name="table">The table.</param>
    /// <returns>The text, and the people it pings.</returns>
    public static (string Text, IReadOnlyList<ulong> Mentions) HostPrompt(TableDto table)
    {
        if (table.HostUserId is not { } host)
        {
            return ($"No host has been picked for **{table.GameName}, table {table.Number}** yet. " +
                    "An organiser can set one on the website; until then, sort the rules out between you.", []);
        }

        return ($"<@{host}>, you're hosting **{table.GameName}, table {table.Number}** ({table.Size} seats). " +
                "You're first in. Get the table set up and teach the rules; when it's done, report the result here.", [(ulong)host]);
    }

    /// <summary>The second message: everyone else who is seated, welcomed into the thread by mention.</summary>
    /// <param name="table">The table.</param>
    /// <returns>The text and the people it pings, or null when nobody else is seated.</returns>
    public static (string Text, IReadOnlyList<ulong> Mentions)? PlayersWelcome(TableDto table)
    {
        var others = table.Seats.Where(s => s.UserId != table.HostUserId).OrderBy(s => s.SeatOrder).Select(s => s.UserId).ToList();
        if (others.Count == 0)
        {
            return null;
        }

        return ($"Welcome {string.Join(", ", others.Select(id => $"<@{id}>"))}. " +
                $"{table.Seats.Count} of {table.Size} seats are taken.", [.. others.Select(id => (ulong)id)]);
    }

    /// <summary>A note that someone sat down at or left the table.</summary>
    /// <param name="payload">What changed.</param>
    /// <param name="seated">How many are seated now.</param>
    /// <param name="size">How many seats the table has.</param>
    /// <returns>The text.</returns>
    public static string SeatChange(TableSeatChangePayload payload, int seated, int size) =>
        payload.Joined
            ? $"🪑 <@{payload.UserId}> sat down ({seated}/{size})."
            : $"👋 <@{payload.UserId}> left the table ({seated}/{size}).";
}
