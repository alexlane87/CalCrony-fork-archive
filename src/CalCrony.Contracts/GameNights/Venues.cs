namespace CalCrony.Contracts.GameNights;

/// <summary>Size limits for venue data (shared so the web can validate the same way the API does).</summary>
public static class VenueLimits
{
    /// <summary>Longest venue name.</summary>
    public const int NameLength = 100;

    /// <summary>Longest venue address.</summary>
    public const int AddressLength = 200;

    /// <summary>Longest area name.</summary>
    public const int AreaNameLength = 60;

    /// <summary>Largest attendee limit a venue default or override may carry.</summary>
    public const int MaxAttendeeLimit = 10_000;

    /// <summary>Most areas one venue may have.</summary>
    public const int MaxAreasPerVenue = 20;

    /// <summary>Most venues one server may have (active or retired).</summary>
    public const int MaxVenuesPerGuild = 200;
}

/// <summary>One part of a venue that can be opened up for extra room (for example "the upstairs").</summary>
/// <param name="Id">The area id.</param>
/// <param name="Name">The area's name.</param>
/// <param name="ExtraCapacity">How many more people the area holds when it is switched on.</param>
/// <param name="Active">False once retired: it can no longer be switched on for new events.</param>
public record VenueAreaDto(Guid Id, string Name, int ExtraCapacity, bool Active);

/// <summary>A reusable venue.</summary>
/// <param name="Id">The venue id.</param>
/// <param name="Name">The venue's name.</param>
/// <param name="Address">The address, when one is set.</param>
/// <param name="DefaultLimit">The attendee limit with no extra areas on; null means no limit.</param>
/// <param name="Active">False once retired.</param>
/// <param name="Areas">The venue's areas.</param>
public record VenueDto(
    Guid Id, string Name, string? Address, int? DefaultLimit, bool Active, IReadOnlyList<VenueAreaDto> Areas);

/// <summary>Creates a venue.</summary>
/// <param name="Name">The venue's name (required).</param>
/// <param name="Address">The address.</param>
/// <param name="DefaultLimit">The attendee limit with no extra areas on; null means no limit.</param>
public record CreateVenueRequest(string Name, string? Address = null, int? DefaultLimit = null);

/// <summary>Edits a venue; null fields are left alone, and the Clear flags remove an optional value.</summary>
/// <param name="Name">A new name.</param>
/// <param name="Address">A new address.</param>
/// <param name="ClearAddress">Remove the address.</param>
/// <param name="DefaultLimit">A new default limit.</param>
/// <param name="ClearDefaultLimit">Remove the default limit (no limit).</param>
/// <param name="Active">Retire (false) or reinstate (true) the venue.</param>
public record UpdateVenueRequest(
    string? Name = null, string? Address = null, bool ClearAddress = false,
    int? DefaultLimit = null, bool ClearDefaultLimit = false, bool? Active = null);

/// <summary>Adds an area to a venue.</summary>
/// <param name="Name">The area's name (required).</param>
/// <param name="ExtraCapacity">How many more people it holds (at least 1).</param>
public record CreateVenueAreaRequest(string Name, int ExtraCapacity);

/// <summary>Edits an area; null fields are left alone.</summary>
/// <param name="Name">A new name.</param>
/// <param name="ExtraCapacity">A new capacity.</param>
/// <param name="Active">Retire (false) or reinstate (true) the area.</param>
public record UpdateVenueAreaRequest(string? Name = null, int? ExtraCapacity = null, bool? Active = null);

/// <summary>Where an event's attendee limit came from.</summary>
public enum LimitSource
{
    /// <summary>No venue and no override: the event keeps whatever limit it already had.</summary>
    None = 0,

    /// <summary>An organiser set the limit for this event.</summary>
    Override = 1,

    /// <summary>The venue's default plus the areas switched on for the event.</summary>
    Venue = 2,
}

/// <summary>An event's effective limit and how it was worked out.</summary>
/// <param name="Limit">The limit, or null for none.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Explanation">A short human description, for example
/// "Venue 30 + the upstairs 12 = 42"; null when the source is None.</param>
public record EffectiveLimitDto(int? Limit, LimitSource Source, string? Explanation);

/// <summary>An event's venue settings.</summary>
/// <param name="EventId">The event.</param>
/// <param name="Venue">The venue, or null when none is attached.</param>
/// <param name="LimitOverride">The organiser's override, or null.</param>
/// <param name="AreaIdsOn">The areas switched on for this event.</param>
/// <param name="Effective">The resulting limit.</param>
public record EventVenueDto(
    Guid EventId, VenueDto? Venue, int? LimitOverride, IReadOnlyList<Guid> AreaIdsOn, EffectiveLimitDto Effective);

/// <summary>Sets an event's venue settings wholesale.</summary>
/// <param name="VenueId">The venue to attach; null detaches it (and switches its areas off).</param>
/// <param name="LimitOverride">A limit for this event that beats the venue; null removes the override.</param>
/// <param name="AreaIdsOn">The areas to switch on (must belong to the venue and be active).</param>
public record SetEventVenueRequest(Guid? VenueId, int? LimitOverride = null, IReadOnlyList<Guid>? AreaIdsOn = null);

/// <summary>Switches one area on or off for an event.</summary>
/// <param name="On">True to switch the area on.</param>
public record SetEventAreaRequest(bool On);

/// <summary>The effective attendee limit rule, shared by the API (which applies it) and clients
/// (which can preview it).</summary>
public static class VenueLimitRules
{
    /// <summary>Works out an event's limit: an override wins; otherwise a venue contributes its
    /// default plus the capacity of the areas switched on; a venue with no default means no limit;
    /// no venue and no override means "leave the event's own limit alone".</summary>
    /// <param name="limitOverride">The event's override, or null.</param>
    /// <param name="venueName">The venue's name, or null when none is attached.</param>
    /// <param name="venueDefault">The venue's default limit, or null for none.</param>
    /// <param name="areasOn">The names and capacities of the active areas switched on.</param>
    /// <returns>The limit, its source and a description.</returns>
    public static EffectiveLimitDto Compute(
        int? limitOverride, string? venueName, int? venueDefault, IReadOnlyList<(string Name, int Capacity)> areasOn)
    {
        if (limitOverride is int fixedLimit)
        {
            return new EffectiveLimitDto(fixedLimit, LimitSource.Override, $"Set for this event: {fixedLimit}");
        }

        if (venueName is null)
        {
            return new EffectiveLimitDto(null, LimitSource.None, null);
        }

        if (venueDefault is not int baseLimit)
        {
            return new EffectiveLimitDto(null, LimitSource.Venue, $"{venueName} has no limit");
        }

        var total = baseLimit + areasOn.Sum(a => a.Capacity);
        var parts = string.Join(" + ", areasOn.Select(a => $"{a.Name} {a.Capacity}").Prepend($"{venueName} {baseLimit}"));
        return new EffectiveLimitDto(total, LimitSource.Venue, areasOn.Count == 0 ? parts : $"{parts} = {total}");
    }
}
