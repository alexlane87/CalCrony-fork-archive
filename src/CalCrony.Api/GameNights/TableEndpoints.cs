using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.GameNights;

/// <summary>Tables at a game night. Organisers open, resize, host and cancel tables; any member may sit
/// down at or leave a table themselves; organisers may seat anyone. Creating a table queues one Discord
/// thread through the outbox, and later seat changes queue a note for it. A table is always inside its
/// game's size range and never above 100 seats.</summary>
public static class TableEndpoints
{
    /// <summary>Maps the table routes onto the Game Nights group.</summary>
    /// <param name="g">The <c>/guilds/{guildId}/game-nights</c> group.</param>
    public static void MapTableEndpoints(this RouteGroupBuilder g)
    {
        g.MapGet("/events/{eventId:guid}/tables", ListTables).RequireTier(GameNightsTier.Member);
        g.MapPost("/events/{eventId:guid}/tables", CreateTable).RequireTier(GameNightsTier.Organiser);
        g.MapPut("/events/{eventId:guid}/table-defaults", SetDefaults).RequireTier(GameNightsTier.Organiser);

        g.MapGet("/tables/{tableId:guid}", GetTable).RequireTier(GameNightsTier.Member);
        g.MapPatch("/tables/{tableId:guid}", UpdateTable).RequireTier(GameNightsTier.Organiser);
        g.MapPost("/tables/{tableId:guid}/cancel", CancelTable).RequireTier(GameNightsTier.Organiser);
        g.MapPost("/tables/{tableId:guid}/seats", Seat).RequireTier(GameNightsTier.Member);
        g.MapDelete("/tables/{tableId:guid}/seats/{userId:long}", Unseat).RequireTier(GameNightsTier.Member);
        g.MapPut("/tables/{tableId:guid}/thread", SetThread).RequireAuthorization("BotOnly").RequireTier(GameNightsTier.Member);
    }

    // ---- reads ----

    private static async Task<IResult> ListTables(
        long guildId, Guid eventId, HttpContext context, TierResolver tiers, CalCronyDbContext db,
        CancellationToken cancellationToken, bool includeCancelled = false)
    {
        var (tier, userId) = await WhoAsync(context, tiers, guildId, cancellationToken);
        var ev = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId && e.GuildId == guildId, cancellationToken);
        if (ev is null || !await VisibilityFilter.CanSeeEventAsync(db, eventId, userId, tier, cancellationToken))
        {
            return Results.NotFound();
        }

        var tables = await db.GameNightTables.AsNoTracking().Include(t => t.Seats)
            .Where(t => t.EventId == eventId && (includeCancelled || t.Status != TableStatus.Cancelled))
            .OrderBy(t => t.Number)
            .ToListAsync(cancellationToken);
        var visible = new List<GameNightTable>();
        foreach (var table in tables)
        {
            if (await CanSeeTableAsync(db, table, userId, tier, cancellationToken))
            {
                visible.Add(table);
            }
        }

        var defaultSize = await db.GameNightEventExtensions.AsNoTracking()
            .Where(x => x.EventId == eventId).Select(x => x.DefaultTableSize).FirstOrDefaultAsync(cancellationToken);
        return Results.Ok(new EventTablesDto(defaultSize, await ToDtosAsync(db, visible, cancellationToken)));
    }

    private static async Task<IResult> GetTable(
        long guildId, Guid tableId, HttpContext context, TierResolver tiers, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var (tier, userId) = await WhoAsync(context, tiers, guildId, cancellationToken);
        var table = await FindTableAsync(db, guildId, tableId, track: false, cancellationToken);
        if (table is null || !await CanSeeTableAsync(db, table, userId, tier, cancellationToken))
        {
            return Results.NotFound();
        }

        return Results.Ok((await ToDtosAsync(db, [table], cancellationToken))[0]);
    }

    // ---- writes ----

    private static async Task<IResult> CreateTable(
        long guildId, Guid eventId, CreateTableRequest request, HttpContext context, TierResolver tiers,
        CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var (tier, userId) = await WhoAsync(context, tiers, guildId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockEventAsync(db, eventId, cancellationToken);
        var ev = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId && e.GuildId == guildId, cancellationToken);
        if (ev is null || !await VisibilityFilter.CanSeeEventAsync(db, eventId, userId, tier, cancellationToken))
        {
            return Results.NotFound();
        }

        if (!IsLive(ev))
        {
            return Results.Conflict(new ErrorResponse("That game night is over, so it can't take new tables."));
        }

        var game = await db.GameNightGames.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.GameId && x.GuildId == guildId, cancellationToken);
        if (game is null || !game.Active)
        {
            return Bad("Choose a game from this server's library (retired games can't be played).");
        }

        var spec = SizeSpec(game);
        var eventDefault = await db.GameNightEventExtensions.AsNoTracking()
            .Where(x => x.EventId == eventId).Select(x => x.DefaultTableSize).FirstOrDefaultAsync(cancellationToken);
        var size = TableRules.ChooseSize(request.Size, eventDefault, spec);
        if (!TableRules.IsAllowedSize(size, spec))
        {
            return Bad($"{game.Name} tables hold {game.MinSize} to {Math.Min(game.MaxSize, GameRules.AbsoluteMaxSize)} players.");
        }

        var seated = (request.SeatUserIds ?? []).Where(id => id > 0).Distinct().ToList();
        if (request.HostUserId is { } host)
        {
            if (host <= 0)
            {
                return Bad("The host is not valid.");
            }

            if (!seated.Contains(host))
            {
                seated.Insert(0, host);
            }
        }

        if (seated.Count > size)
        {
            return Bad($"That's {seated.Count} people for a table of {size}.");
        }

        var number = (await db.GameNightTables.Where(t => t.EventId == eventId).MaxAsync(t => (int?)t.Number, cancellationToken) ?? 0) + 1;
        var table = new GameNightTable
        {
            Id = Guid.NewGuid(), EventId = eventId, GameId = game.Id, Number = number, Size = size, HostUserId = request.HostUserId,
        };
        db.GameNightTables.Add(table);
        for (var i = 0; i < seated.Count; i++)
        {
            db.GameNightTableSeats.Add(new GameNightTableSeat { Id = Guid.NewGuid(), TableId = table.Id, UserId = seated[i], SeatOrder = i });
        }

        // One thread per table, queued with the table itself so a crash can't leave one without the other.
        GameNightOutbox.Enqueue(db, clock, DeliveryType.CreateTableThread, ev.ChannelId, new CreateTableThreadPayload(table.Id, guildId));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var loaded = (await FindTableAsync(db, guildId, table.Id, track: false, cancellationToken))!;
        return Results.Created($"/guilds/{guildId}/game-nights/tables/{table.Id}", (await ToDtosAsync(db, [loaded], cancellationToken))[0]);
    }

    private static async Task<IResult> UpdateTable(
        long guildId, Guid tableId, UpdateTableRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (request.HostUserId is not null && request.ClearHost)
        {
            return Bad("Choose a host or clear the host, not both.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockedFor = await FindTableAsync(db, guildId, tableId, track: false, cancellationToken);
        if (lockedFor is null)
        {
            return Results.NotFound();
        }

        await LockEventAsync(db, lockedFor.EventId, cancellationToken);
        var table = (await FindTableAsync(db, guildId, tableId, track: true, cancellationToken))!;
        if (table.Status != TableStatus.Open)
        {
            return Results.Conflict(new ErrorResponse("This table is closed, so it can't be changed."));
        }

        if (request.Size is { } size)
        {
            var game = await db.GameNightGames.AsNoTracking().FirstAsync(x => x.Id == table.GameId, cancellationToken);
            if (!TableRules.IsAllowedSize(size, SizeSpec(game)))
            {
                return Bad($"{game.Name} tables hold {game.MinSize} to {Math.Min(game.MaxSize, GameRules.AbsoluteMaxSize)} players.");
            }

            if (size < table.Seats.Count)
            {
                return Results.Conflict(new ErrorResponse($"{table.Seats.Count} people are already seated, so the table can't shrink below that."));
            }

            table.Size = size;
        }

        if (request.ClearHost)
        {
            table.HostUserId = null;
        }
        else if (request.HostUserId is { } host)
        {
            if (table.Seats.All(s => s.UserId != host))
            {
                return Bad("The host has to be sitting at the table.");
            }

            table.HostUserId = host;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok((await ToDtosAsync(db, [table], cancellationToken))[0]);
    }

    private static async Task<IResult> CancelTable(
        long guildId, Guid tableId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var table = await FindTableAsync(db, guildId, tableId, track: true, cancellationToken);
        if (table is null)
        {
            return Results.NotFound();
        }

        if (table.Status == TableStatus.Played)
        {
            return Results.Conflict(new ErrorResponse("A table with a confirmed result can't be cancelled."));
        }

        table.Status = TableStatus.Cancelled;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok((await ToDtosAsync(db, [table], cancellationToken))[0]);
    }

    private static async Task<IResult> Seat(
        long guildId, Guid tableId, SeatRequest request, HttpContext context, TierResolver tiers, CalCronyDbContext db,
        IClock clock, CancellationToken cancellationToken)
    {
        var (tier, userId) = await WhoAsync(context, tiers, guildId, cancellationToken);
        if (request.UserId <= 0)
        {
            return Bad("The player is not valid.");
        }

        if (!MayActFor(context, tier, userId, request.UserId))
        {
            return Results.Json(new ErrorResponse("You can only sit yourself down; an organiser can seat other people."), statusCode: 403);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var preview = await FindTableAsync(db, guildId, tableId, track: false, cancellationToken);
        if (preview is null || !await CanSeeTableAsync(db, preview, userId, tier, cancellationToken))
        {
            return Results.NotFound();
        }

        await LockEventAsync(db, preview.EventId, cancellationToken);
        var table = (await FindTableAsync(db, guildId, tableId, track: true, cancellationToken))!;
        var ev = await db.Events.AsNoTracking().FirstAsync(e => e.Id == table.EventId, cancellationToken);
        if (table.Status != TableStatus.Open || !IsLive(ev))
        {
            return Results.Conflict(new ErrorResponse("This table is closed."));
        }

        if (table.Seats.Any(s => s.UserId == request.UserId))
        {
            return Results.Conflict(new ErrorResponse("That person is already sitting at this table."));
        }

        if (table.Seats.Count >= table.Size)
        {
            return Results.Conflict(new ErrorResponse("This table is full."));
        }

        db.GameNightTableSeats.Add(new GameNightTableSeat
        {
            Id = Guid.NewGuid(), TableId = table.Id, UserId = request.UserId,
            SeatOrder = table.Seats.Count == 0 ? 0 : table.Seats.Max(s => s.SeatOrder) + 1,
        });
        if (table.ThreadId is not null)
        {
            GameNightOutbox.Enqueue(db, clock, DeliveryType.TableSeatChange, ev.ChannelId, new TableSeatChangePayload(table.Id, guildId, request.UserId, Joined: true));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var after = (await FindTableAsync(db, guildId, tableId, track: false, cancellationToken))!;
        return Results.Ok((await ToDtosAsync(db, [after], cancellationToken))[0]);
    }

    private static async Task<IResult> Unseat(
        long guildId, Guid tableId, long userId, HttpContext context, TierResolver tiers, CalCronyDbContext db,
        IClock clock, CancellationToken cancellationToken)
    {
        var (tier, caller) = await WhoAsync(context, tiers, guildId, cancellationToken);
        if (!MayActFor(context, tier, caller, userId))
        {
            return Results.Json(new ErrorResponse("You can only stand yourself up; an organiser can move other people."), statusCode: 403);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var preview = await FindTableAsync(db, guildId, tableId, track: false, cancellationToken);
        if (preview is null || !await CanSeeTableAsync(db, preview, caller, tier, cancellationToken))
        {
            return Results.NotFound();
        }

        await LockEventAsync(db, preview.EventId, cancellationToken);
        var table = (await FindTableAsync(db, guildId, tableId, track: true, cancellationToken))!;
        if (table.Status != TableStatus.Open)
        {
            return Results.Conflict(new ErrorResponse("This table is closed."));
        }

        var seat = table.Seats.FirstOrDefault(s => s.UserId == userId);
        if (seat is null)
        {
            return Results.Conflict(new ErrorResponse("That person isn't sitting at this table."));
        }

        db.GameNightTableSeats.Remove(seat);
        if (table.HostUserId == userId)
        {
            table.HostUserId = null; // the host is always seated, so leaving gives the hosting up
        }

        if (table.ThreadId is not null)
        {
            var ev = await db.Events.AsNoTracking().FirstAsync(e => e.Id == table.EventId, cancellationToken);
            GameNightOutbox.Enqueue(db, clock, DeliveryType.TableSeatChange, ev.ChannelId, new TableSeatChangePayload(table.Id, guildId, userId, Joined: false));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var after = (await FindTableAsync(db, guildId, tableId, track: false, cancellationToken))!;
        return Results.Ok((await ToDtosAsync(db, [after], cancellationToken))[0]);
    }

    private static async Task<IResult> SetDefaults(
        long guildId, Guid eventId, SetTableDefaultsRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (request.DefaultTableSize is { } size && (size < GameRules.AbsoluteMinSize || size > GameRules.AbsoluteMaxSize))
        {
            return Bad($"A default table size must be between {GameRules.AbsoluteMinSize} and {GameRules.AbsoluteMaxSize}.");
        }

        if (!await db.Events.AnyAsync(e => e.Id == eventId && e.GuildId == guildId, cancellationToken))
        {
            return Results.NotFound();
        }

        var extension = await db.GameNightEventExtensions.FirstOrDefaultAsync(x => x.EventId == eventId, cancellationToken);
        if (extension is null)
        {
            extension = new GameNightEventExtension { EventId = eventId };
            db.GameNightEventExtensions.Add(extension);
        }

        extension.DefaultTableSize = request.DefaultTableSize;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new SetTableDefaultsRequest(extension.DefaultTableSize));
    }

    private static async Task<IResult> SetThread(
        long guildId, Guid tableId, SetTableThreadRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var table = await FindTableAsync(db, guildId, tableId, track: true, cancellationToken);
        if (table is null)
        {
            return Results.NotFound();
        }

        table.ThreadId = request.ThreadId;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok((await ToDtosAsync(db, [table], cancellationToken))[0]);
    }

    // ---- helpers ----

    internal static async Task<(GameNightsTier Tier, long UserId)> WhoAsync(
        HttpContext context, TierResolver tiers, long guildId, CancellationToken cancellationToken) =>
        ((await tiers.ResolveAsync(context.User, guildId, cancellationToken)).Tier, context.User.WebUserId() ?? 0);

    /// <summary>The bot is trusted (it checks Discord before calling); an organiser may act for anyone; a
    /// member may act only for themselves.</summary>
    private static bool MayActFor(HttpContext context, GameNightsTier tier, long caller, long subject) =>
        context.User.IsBot() || tier >= GameNightsTier.Organiser || (caller != 0 && caller == subject);

    private static bool IsLive(Event ev) => ev.Status is EventStatus.Scheduled or EventStatus.Started;

    internal static Task LockEventAsync(CalCronyDbContext db, Guid eventId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"""SELECT "Id" FROM "Events" WHERE "Id" = {eventId} FOR UPDATE""", cancellationToken);

    internal static async Task<GameNightTable?> FindTableAsync(
        CalCronyDbContext db, long guildId, Guid tableId, bool track, CancellationToken cancellationToken)
    {
        var query = db.GameNightTables.Include(t => t.Seats).AsQueryable();
        if (!track)
        {
            query = query.AsNoTracking();
        }

        return await query
            .Where(t => t.Id == tableId)
            .Where(t => db.Events.Any(e => e.Id == t.EventId && e.GuildId == guildId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>A table is visible when its night is, and the table itself is public, or the caller is
    /// sitting at it or invited, or their tier reaches it. Hidden is reported as not found.</summary>
    internal static async Task<bool> CanSeeTableAsync(
        CalCronyDbContext db, GameNightTable table, long userId, GameNightsTier tier, CancellationToken cancellationToken)
    {
        if (!await VisibilityFilter.CanSeeEventAsync(db, table.EventId, userId, tier, cancellationToken))
        {
            return false;
        }

        if (table.Visibility == GameNightsVisibility.Public || (userId != 0 && table.Seats.Any(s => s.UserId == userId)))
        {
            return true;
        }

        var invited = userId != 0 && await db.GameNightInvites.AnyAsync(
            i => i.Scope == GameNightInviteScope.Table && i.ScopeId == table.Id && i.UserId == userId, cancellationToken);
        return VisibilityRules.CanSee(table.Visibility, tier, invited);
    }

    private static GameSpec SizeSpec(GameNightGame game) => new(
        game.Key, game.Name, game.ResultType, game.MinSize, game.MaxSize, game.DefaultSize,
        game.RatingEnabled, game.RatingK, game.StartRating, []);

    private static async Task<IReadOnlyList<TableDto>> ToDtosAsync(
        CalCronyDbContext db, IReadOnlyList<GameNightTable> tables, CancellationToken cancellationToken)
    {
        var gameIds = tables.Select(t => t.GameId).Distinct().ToList();
        var names = await db.GameNightGames.AsNoTracking().Where(g => gameIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        return [.. tables.Select(t => new TableDto(
            t.Id, t.EventId, t.GameId, names.GetValueOrDefault(t.GameId, "Game"), t.Number, t.Size, t.HostUserId, t.Status,
            t.ThreadId, t.Visibility,
            [.. t.Seats.Where(s => s.UserId is not null).OrderBy(s => s.SeatOrder).Select(s => new TableSeatDto(s.UserId!.Value, s.SeatOrder))]))];
    }

    private static IResult Bad(string message) => Results.BadRequest(new ErrorResponse(message));
}
