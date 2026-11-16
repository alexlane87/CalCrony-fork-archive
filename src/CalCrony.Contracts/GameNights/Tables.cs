namespace CalCrony.Contracts.GameNights;

/// <summary>Where a table is in its life. Stored and sent as the number.</summary>
public enum TableStatus
{
    /// <summary>Being filled or played.</summary>
    Open = 0,

    /// <summary>A result has been confirmed (set by result reporting in a later step).</summary>
    Played = 1,

    /// <summary>Called off by an organiser.</summary>
    Cancelled = 2,
}

/// <summary>One seat at a table. Phase 1 seats Discord members; guests arrive with result reporting.</summary>
/// <param name="UserId">The seated member's Discord user id.</param>
/// <param name="SeatOrder">Position in the order people sat down (0 is first).</param>
public record TableSeatDto(long UserId, int SeatOrder);

/// <summary>A table at a game night.</summary>
/// <param name="Id">The table id.</param>
/// <param name="EventId">The game night.</param>
/// <param name="GameId">The game being played.</param>
/// <param name="GameName">The game's name.</param>
/// <param name="Number">1, 2, 3... within the night.</param>
/// <param name="Size">How many seats.</param>
/// <param name="HostUserId">The host (who teaches and runs the table), if there is one; always seated.</param>
/// <param name="Status">Open, played or cancelled.</param>
/// <param name="ThreadId">The Discord thread for the table, once the bot has made it.</param>
/// <param name="Visibility">Who can see it. Public in phase 1.</param>
/// <param name="Seats">Who is sitting, in the order they sat.</param>
public record TableDto(
    Guid Id, Guid EventId, Guid GameId, string GameName, int Number, int Size, long? HostUserId, TableStatus Status,
    long? ThreadId, GameNightsVisibility Visibility, IReadOnlyList<TableSeatDto> Seats);

/// <summary>A night's tables and its default table size.</summary>
/// <param name="DefaultTableSize">The size new tables start at on this night, or null to use each game's own.</param>
/// <param name="Tables">The tables the caller may see.</param>
public record EventTablesDto(int? DefaultTableSize, IReadOnlyList<TableDto> Tables);

/// <summary>Opens a table.</summary>
/// <param name="GameId">The game.</param>
/// <param name="Size">The number of seats; null uses the night's default, then the game's.</param>
/// <param name="HostUserId">The host, if any; they are seated automatically.</param>
/// <param name="SeatUserIds">People to seat straight away.</param>
public record CreateTableRequest(Guid GameId, int? Size = null, long? HostUserId = null, IReadOnlyList<long>? SeatUserIds = null);

/// <summary>Changes a table.</summary>
/// <param name="Size">A new number of seats (not below the seated count, not outside the game's range).</param>
/// <param name="HostUserId">A new host, who must be seated.</param>
/// <param name="ClearHost">Remove the host.</param>
public record UpdateTableRequest(int? Size = null, long? HostUserId = null, bool ClearHost = false);

/// <summary>Seats a person.</summary>
/// <param name="UserId">The Discord user id. A member may only seat themselves; organisers may seat anyone.</param>
public record SeatRequest(long UserId);

/// <summary>Sets the night's default table size.</summary>
/// <param name="DefaultTableSize">The size, or null to clear it.</param>
public record SetTableDefaultsRequest(int? DefaultTableSize);

/// <summary>Records the Discord thread the bot made for a table (bot only).</summary>
/// <param name="ThreadId">The thread id, or null to clear a thread that was deleted in Discord.</param>
public record SetTableThreadRequest(long? ThreadId);

/// <summary>Outbox payload: make the table's Discord thread (once) and open it with the host prompt and
/// the seated players mentioned. The bot reads the table from the API, so the payload is just the id and the
/// delivery is safe to repeat.</summary>
/// <param name="TableId">The table.</param>
/// <param name="GuildId">The Discord guild (server) id.</param>
public record CreateTableThreadPayload(Guid TableId, long GuildId);

/// <summary>Outbox payload: someone sat down at, or left, a table that already has a thread.</summary>
/// <param name="TableId">The table.</param>
/// <param name="GuildId">The Discord guild (server) id.</param>
/// <param name="UserId">The person.</param>
/// <param name="Joined">True if they sat down, false if they left.</param>
public record TableSeatChangePayload(Guid TableId, long GuildId, long UserId, bool Joined);

/// <summary>Table size rules shared by the API and clients.</summary>
public static class TableRules
{
    /// <summary>The size a new table gets: the request's, else the night's default (kept inside the game's
    /// range), else the game's own default.</summary>
    /// <param name="requested">The size asked for, or null.</param>
    /// <param name="eventDefault">The night's default, or null.</param>
    /// <param name="game">The game.</param>
    /// <returns>The size.</returns>
    public static int ChooseSize(int? requested, int? eventDefault, GameSpec game) =>
        requested ?? (eventDefault is int d ? Math.Clamp(d, game.MinSize, game.MaxSize) : game.DefaultSize);

    /// <summary>Whether a size is allowed for a game: inside its range and never above 100.</summary>
    /// <param name="size">The size.</param>
    /// <param name="game">The game.</param>
    /// <returns>True when allowed.</returns>
    public static bool IsAllowedSize(int size, GameSpec game) =>
        size >= game.MinSize && size <= Math.Min(game.MaxSize, GameRules.AbsoluteMaxSize);
}
