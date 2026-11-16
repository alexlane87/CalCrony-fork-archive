using CalCrony.Contracts.GameNights;

namespace CalCrony.Api.GameNights;

/// <summary>A game a server's members play, defined as data (see <see cref="GameSpec"/>). Unique per
/// (GuildId, Key). Retired, never deleted, so old tables and results keep their game.</summary>
public class GameNightGame
{
    /// <summary>The game id.</summary>
    public Guid Id { get; set; }

    /// <summary>The Discord guild (server) id.</summary>
    public long GuildId { get; set; }

    /// <summary>The stable key (for example "root").</summary>
    public string Key { get; set; } = "";

    /// <summary>The game's name.</summary>
    public string Name { get; set; } = "";

    /// <summary>How its result is recorded.</summary>
    public GameResultType ResultType { get; set; }

    /// <summary>The fewest players at a table.</summary>
    public int MinSize { get; set; }

    /// <summary>The most players at a table.</summary>
    public int MaxSize { get; set; }

    /// <summary>The size a new table starts at.</summary>
    public int DefaultSize { get; set; }

    /// <summary>Whether results feed a league rating.</summary>
    public bool RatingEnabled { get; set; }

    /// <summary>The rating K-factor.</summary>
    public int RatingK { get; set; }

    /// <summary>The rating a player starts at.</summary>
    public int StartRating { get; set; }

    /// <summary>The Discord role members take to follow the game, if any.</summary>
    public long? RoleId { get; set; }

    /// <summary>False once retired.</summary>
    public bool Active { get; set; } = true;

    /// <summary>The version of the config-file entry this game was last seeded from; null for a game
    /// created by hand.</summary>
    public int? SeedVersion { get; set; }

    /// <summary>True once someone has edited a seeded game, so a newer file version no longer overwrites it.</summary>
    public bool LocallyEdited { get; set; }

    /// <summary>Whether the follow role is being made, or the bot failed to make or grant it.</summary>
    public GameRoleState RoleState { get; set; }

    /// <summary>Why the role failed, in words an admin can act on.</summary>
    public string? RoleNote { get; set; }

    /// <summary>The result questions.</summary>
    public List<GameNightGameField> Fields { get; set; } = [];

    /// <summary>The game-type roles the game belongs to.</summary>
    public List<GameNightGameTypeLink> TypeLinks { get; set; } = [];
}

/// <summary>One result question of a game.</summary>
public class GameNightGameField
{
    /// <summary>The field id.</summary>
    public Guid Id { get; set; }

    /// <summary>The game it belongs to.</summary>
    public Guid GameId { get; set; }

    /// <summary>Position in the game's question list.</summary>
    public int SortOrder { get; set; }

    /// <summary>The stable key (for example "faction").</summary>
    public string Key { get; set; } = "";

    /// <summary>What the reporter sees.</summary>
    public string Label { get; set; } = "";

    /// <summary>The kind of answer.</summary>
    public GameFieldKind Kind { get; set; }

    /// <summary>Once per table or once per player.</summary>
    public GameFieldScope Scope { get; set; }

    /// <summary>Whether the report needs it.</summary>
    public bool Required { get; set; }

    /// <summary>Whether no two players may give the same answer.</summary>
    public bool Unique { get; set; }

    /// <summary>The allowed values of a pick-list.</summary>
    public string[] Values { get; set; } = [];
}

/// <summary>A Discord role an admin has marked as a game type, for one server.</summary>
public class GameNightGameType
{
    /// <summary>The Discord guild (server) id.</summary>
    public long GuildId { get; set; }

    /// <summary>The Discord role id.</summary>
    public long RoleId { get; set; }

    /// <summary>A name for the type, as the admin typed it.</summary>
    public string Label { get; set; } = "";
}

/// <summary>Links a game to a game-type role it belongs to.</summary>
public class GameNightGameTypeLink
{
    /// <summary>The game.</summary>
    public Guid GameId { get; set; }

    /// <summary>The game-type role id (a role listed in <see cref="GameNightGameType"/> for the same server).</summary>
    public long TypeRoleId { get; set; }
}

/// <summary>A table at a game night: one game, a number of seats, an optional host, and (once the bot has
/// made it) a Discord thread. Cancelled, never deleted, so its history stays.</summary>
public class GameNightTable
{
    /// <summary>The table id.</summary>
    public Guid Id { get; set; }

    /// <summary>The game night.</summary>
    public Guid EventId { get; set; }

    /// <summary>The game being played.</summary>
    public Guid GameId { get; set; }

    /// <summary>1, 2, 3... within the night (unique per night).</summary>
    public int Number { get; set; }

    /// <summary>How many seats.</summary>
    public int Size { get; set; }

    /// <summary>The host, if any; always one of the seated members.</summary>
    public long? HostUserId { get; set; }

    /// <summary>Open, played or cancelled.</summary>
    public TableStatus Status { get; set; } = TableStatus.Open;

    /// <summary>The Discord thread, once made.</summary>
    public long? ThreadId { get; set; }

    /// <summary>Who may see the table. Public by default; a table that is not public can never carry a league.</summary>
    public GameNightsVisibility Visibility { get; set; } = GameNightsVisibility.Public;

    /// <summary>Who is sitting.</summary>
    public List<GameNightTableSeat> Seats { get; set; } = [];
}

/// <summary>One occupied seat. Exactly one of <see cref="UserId"/> and <see cref="GuestId"/> is set.</summary>
public class GameNightTableSeat
{
    /// <summary>The seat id.</summary>
    public Guid Id { get; set; }

    /// <summary>The table.</summary>
    public Guid TableId { get; set; }

    /// <summary>A Discord member.</summary>
    public long? UserId { get; set; }

    /// <summary>A guest (no Discord account); used from result reporting onwards.</summary>
    public Guid? GuestId { get; set; }

    /// <summary>The order people sat down in.</summary>
    public int SeatOrder { get; set; }
}
