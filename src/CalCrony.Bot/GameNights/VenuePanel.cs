using CalCrony.Contracts.GameNights;
using Discord;

namespace CalCrony.Bot.GameNights;

/// <summary>The organiser's private panel for an event's venue: the limit and where it came from,
/// and one toggle button per area. Pure, so it is testable without Discord.</summary>
public static class VenuePanel
{
    /// <summary>Custom-id prefix of the area buttons: <c>gnarea:{eventId}:{areaId}:{1|0}</c>, where the
    /// last segment is the state the press asks for.</summary>
    public const string AreaButtonPrefix = "gnarea";

    private const int ButtonsPerRow = 5;

    /// <summary>Builds the panel.</summary>
    /// <param name="eventTitle">The event's title.</param>
    /// <param name="venue">The event's venue settings (must have a venue).</param>
    /// <returns>The embed and the area buttons (null when the venue has no active areas).</returns>
    public static (Embed Embed, MessageComponent? Components) Build(string eventTitle, EventVenueDto venue)
    {
        var description = venue.Venue is { } v
            ? string.IsNullOrWhiteSpace(v.Address) ? $"📍 **{v.Name}**" : $"📍 **{v.Name}**, {v.Address}"
            : "No venue attached.";
        var limit = venue.Effective.Limit is int n ? $"**{n}** places" : "No limit";
        var source = venue.Effective.Explanation is { } explanation ? $"\n{explanation}" : "";

        var embed = new EmbedBuilder()
            .WithTitle($"Venue: {Trim(eventTitle, 200)}")
            .WithDescription($"{description}\nAttendee limit: {limit}{source}")
            .WithFooter("Opening an area adds its places and seats anyone waiting. Closing one can't drop below the seats taken.")
            .Build();

        var areas = (venue.Venue?.Areas ?? []).Where(a => a.Active).ToList();
        if (areas.Count == 0)
        {
            return (embed, null);
        }

        var on = venue.AreaIdsOn.ToHashSet();
        var components = new ComponentBuilder();
        for (var i = 0; i < areas.Count && i < ButtonsPerRow * 5; i++)
        {
            var area = areas[i];
            var isOn = on.Contains(area.Id);
            components.WithButton(
                label: Trim($"{(isOn ? "✓ " : "")}{area.Name} +{area.ExtraCapacity}", 80),
                customId: $"{AreaButtonPrefix}:{venue.EventId}:{area.Id}:{(isOn ? 0 : 1)}",
                style: isOn ? ButtonStyle.Success : ButtonStyle.Secondary,
                row: i / ButtonsPerRow);
        }

        return (embed, components.Build());
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
