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
