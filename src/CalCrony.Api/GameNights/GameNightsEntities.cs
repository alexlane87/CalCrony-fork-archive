using CalCrony.Contracts.GameNights;

namespace CalCrony.Api.GameNights;

/// <summary>A Discord role mapped to a Game Nights tier for one guild. Composite key
/// (GuildId, RoleId): a role maps to at most one tier. The role ids join the bot's watched-role set
/// (see RoleWatchList) so their holders are snapshotted.</summary>
public class GameNightsTierRole
{
    /// <summary>The Discord guild (server) id.</summary>
    public long GuildId { get; set; }

    /// <summary>The Discord role id.</summary>
    public long RoleId { get; set; }

    /// <summary>The tier a holder gets; never Member.</summary>
    public GameNightsTier Tier { get; set; }
}

/// <summary>The Game Nights side of an event: a one-to-one extension row, so the core Event class is
/// untouched. No row means defaults (public). Step 2 adds the venue override here.</summary>
public class GameNightEventExtension
{
    /// <summary>The event this row extends (primary key; cascades with the event).</summary>
    public Guid EventId { get; set; }

    /// <summary>Who may see the event. Public by default.</summary>
    public GameNightsVisibility Visibility { get; set; } = GameNightsVisibility.Public;

    /// <summary>The venue the event is held at, if any.</summary>
    public Guid? VenueId { get; set; }

    /// <summary>An organiser-set attendee limit for this event that beats the venue's.</summary>
    public int? LimitOverride { get; set; }
}

/// <summary>What an invite list entry is attached to.</summary>
public enum GameNightInviteScope
{
    /// <summary>A game-night event (<see cref="GameNightEventExtension.EventId"/>).</summary>
    Event = 0,

    /// <summary>A table (added in Step 4).</summary>
    Table = 1,
}

/// <summary>One invitee of a restricted or invite-only event or table. No UI in phase 1; the rows
/// exist so the visibility filter has something to read.</summary>
public class GameNightInvite
{
    /// <summary>What the invite is attached to.</summary>
    public GameNightInviteScope Scope { get; set; }

    /// <summary>The event or table id.</summary>
    public Guid ScopeId { get; set; }

    /// <summary>The invited Discord user id.</summary>
    public long UserId { get; set; }
}

/// <summary>A reusable venue. Retired (not deleted) once events have used it.</summary>
public class GameNightVenue
{
    /// <summary>The venue id.</summary>
    public Guid Id { get; set; }

    /// <summary>The Discord guild (server) id.</summary>
    public long GuildId { get; set; }

    /// <summary>The venue's name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The address, when known.</summary>
    public string? Address { get; set; }

    /// <summary>The attendee limit with no extra areas on; null means no limit.</summary>
    public int? DefaultLimit { get; set; }

    /// <summary>False once retired.</summary>
    public bool Active { get; set; } = true;

    /// <summary>The venue's areas.</summary>
    public List<GameNightVenueArea> Areas { get; set; } = [];
}

/// <summary>A part of a venue that adds capacity when switched on for an event.</summary>
public class GameNightVenueArea
{
    /// <summary>The area id.</summary>
    public Guid Id { get; set; }

    /// <summary>The venue it belongs to.</summary>
    public Guid VenueId { get; set; }

    /// <summary>The area's name.</summary>
    public string Name { get; set; } = "";

    /// <summary>How many more people it holds when on.</summary>
    public int ExtraCapacity { get; set; }

    /// <summary>False once retired.</summary>
    public bool Active { get; set; } = true;
}

/// <summary>An area switched on for one event.</summary>
public class GameNightEventArea
{
    /// <summary>The event.</summary>
    public Guid EventId { get; set; }

    /// <summary>The area.</summary>
    public Guid AreaId { get; set; }
}
