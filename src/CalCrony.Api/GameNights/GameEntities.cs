using CalCrony.Contracts.GameNights;

namespace CalCrony.Api.GameNights;

/// <summary>One member's answer about one game for one night: can teach it, can play it, is new to it, or is
/// skipping it. Exactly one answer per (night, member, game); changing it updates the row.</summary>
public class GameNightSignal
{
    /// <summary>The game night.</summary>
    public Guid EventId { get; set; }

    /// <summary>The member's Discord user id.</summary>
    public long UserId { get; set; }

    /// <summary>The game.</summary>
    public Guid GameId { get; set; }

    /// <summary>The answer.</summary>
    public SignalKind Kind { get; set; }
}

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

    /// <summary>Whether a result report must say the board photo has been posted before it can be submitted.</summary>
    public bool PhotoRequired { get; set; }

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

/// <summary>A reported result for a table. A table has at most one result that is pending, confirmed or
/// disputed at a time; a voided one frees the table to report again. Nothing here holds a photo or a link to one.</summary>
public class GameNightResult
{
    /// <summary>The result id.</summary>
    public Guid Id { get; set; }

    /// <summary>The table.</summary>
    public Guid TableId { get; set; }

    /// <summary>The game (the table's game when it was reported).</summary>
    public Guid GameId { get; set; }

    /// <summary>Where it stands.</summary>
    public ResultStatus Status { get; set; }

    /// <summary>Who reported it (a Discord user id).</summary>
    public long ReporterUserId { get; set; }

    /// <summary>When it was reported.</summary>
    public NodaTime.Instant SubmittedAt { get; set; }

    /// <summary>When it was confirmed.</summary>
    public NodaTime.Instant? ConfirmedAt { get; set; }

    /// <summary>The Discord message the bot posted for it in the table's thread (so it can be updated when it settles).</summary>
    public long? SummaryMessageId { get; set; }

    /// <summary>Who played.</summary>
    public List<GameNightResultPlayer> Players { get; set; } = [];

    /// <summary>The table-wide answers.</summary>
    public List<GameNightResultValue> Values { get; set; } = [];

    /// <summary>The players' answers to it.</summary>
    public List<GameNightResultConfirmation> Confirmations { get; set; } = [];
}

/// <summary>One player in a result: a member (Discord id) or a guest. Only the id is stored for a member; the
/// name is worked out when shown.</summary>
public class GameNightResultPlayer
{
    /// <summary>The row id.</summary>
    public Guid Id { get; set; }

    /// <summary>The result.</summary>
    public Guid ResultId { get; set; }

    /// <summary>A Discord member.</summary>
    public long? UserId { get; set; }

    /// <summary>A guest.</summary>
    public Guid? GuestId { get; set; }

    /// <summary>Finishing place.</summary>
    public int? Placement { get; set; }

    /// <summary>The final score (the answer to the question keyed "score").</summary>
    public long? Score { get; set; }

    /// <summary>The other per-player answers as a small JSON object.</summary>
    public string? ValuesJson { get; set; }
}

/// <summary>A table-wide answer in a result (map, deck, landmarks, round the game ended).</summary>
public class GameNightResultValue
{
    /// <summary>The result.</summary>
    public Guid ResultId { get; set; }

    /// <summary>The question key.</summary>
    public string Key { get; set; } = "";

    /// <summary>The answer.</summary>
    public string Value { get; set; } = "";
}

/// <summary>A member's answer to a result.</summary>
public class GameNightResultConfirmation
{
    /// <summary>The result.</summary>
    public Guid ResultId { get; set; }

    /// <summary>The member.</summary>
    public long UserId { get; set; }

    /// <summary>Their answer.</summary>
    public ConfirmationDecision Decision { get; set; }

    /// <summary>When they answered.</summary>
    public NodaTime.Instant At { get; set; }
}

/// <summary>Someone with no Discord account who played. The typed "First L" name is the only personal detail kept,
/// and it can be erased on request (the row stays so results keep their shape).</summary>
public class GameNightGuest
{
    /// <summary>The guest id.</summary>
    public Guid Id { get; set; }

    /// <summary>The Discord guild (server) id.</summary>
    public long GuildId { get; set; }

    /// <summary>The typed name, or "Former guest" once erased.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>True once the name has been erased.</summary>
    public bool Erased { get; set; }
}

/// <summary>A member's "First L" name as the bot worked it out from their server nickname. Only people who sit at
/// tables or appear in results get one, and only the derived form is stored: never the nickname, username or
/// anything else from Discord.</summary>
public class GameNightMemberName
{
    /// <summary>The Discord guild (server) id.</summary>
    public long GuildId { get; set; }

    /// <summary>The member.</summary>
    public long UserId { get; set; }

    /// <summary>"First L", or null when their nickname does not follow the convention.</summary>
    public string? DisplayName { get; set; }

    /// <summary>True once the member has left the server.</summary>
    public bool Left { get; set; }

    /// <summary>When the bot last looked.</summary>
    public NodaTime.Instant UpdatedAt { get; set; }
}
