using System.Text.Json;
using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.GameNights;

/// <summary>A game night's answers: each member says, per game, whether they can teach it, can play it, are new
/// to it, or are skipping it. Answers add up to the demand panel, the teacher roster and the summary on the
/// event post. Anyone who can see the night can answer for themselves; the bot answers on a member's behalf (it
/// checks Discord first). Every change queues ONE delayed re-render of the event post however many answers
/// arrive in the window, so a busy night produces a trickle of Discord edits, not a flood.</summary>
public static class SignalEndpoints
{
    /// <summary>The configuration key for how long a re-render of the event post is held back so changes batch up.</summary>
    public const string IntervalKey = "GameNights:EventPostIntervalSeconds";

    /// <summary>The default hold-back: one edit of an event post per this many seconds, at most.</summary>
    public const int DefaultIntervalSeconds = 20;

    /// <summary>Maps the signal routes onto the Game Nights group.</summary>
    /// <param name="g">The <c>/guilds/{guildId}/game-nights</c> group.</param>
    public static void MapSignalEndpoints(this RouteGroupBuilder g)
    {
        g.MapGet("/events/{eventId:guid}/signals", GetSummary).RequireTier(GameNightsTier.Member);
        g.MapGet("/events/{eventId:guid}/signals/roster", GetRoster).RequireTier(GameNightsTier.Member);
        g.MapGet("/events/{eventId:guid}/signals/people", GetPeople).RequireTier(GameNightsTier.Organiser);
        g.MapGet("/events/{eventId:guid}/signals/mine", GetMine).RequireTier(GameNightsTier.Member);
        g.MapPut("/events/{eventId:guid}/signals/mine", SetMine).RequireTier(GameNightsTier.Member);
        g.MapPut("/events/{eventId:guid}/signals/{gameId:guid}", SetOne).RequireTier(GameNightsTier.Member);
    }

    // ---- reads ----

    private static async Task<IResult> GetSummary(
        long guildId, Guid eventId, HttpContext context, TierResolver tiers, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (await FindVisibleEventAsync(db, context, tiers, guildId, eventId, cancellationToken) is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(await NightSummaries.SummarizeAsync(db, guildId, eventId, cancellationToken));
    }

    private static async Task<IResult> GetRoster(
        long guildId, Guid eventId, HttpContext context, TierResolver tiers, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (await FindVisibleEventAsync(db, context, tiers, guildId, eventId, cancellationToken) is null)
        {
            return Results.NotFound();
        }

        var games = await ActiveGamesAsync(db, guildId, cancellationToken);
        var signals = await PeopleAsync(db, eventId, cancellationToken);
        return Results.Ok(SignalMath.Roster(games, signals));
    }

    private static async Task<IResult> GetPeople(
        long guildId, Guid eventId, HttpContext context, TierResolver tiers, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (await FindVisibleEventAsync(db, context, tiers, guildId, eventId, cancellationToken) is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(await PeopleAsync(db, eventId, cancellationToken));
    }

    private static async Task<IResult> GetMine(
        long guildId, Guid eventId, long? userId, HttpContext context, TierResolver tiers, CalCronyDbContext db,
        CancellationToken cancellationToken)
    {
        if (await FindVisibleEventAsync(db, context, tiers, guildId, eventId, cancellationToken) is null)
        {
            return Results.NotFound();
        }

        if (Subject(context, userId) is not { } subject)
        {
            return Bad("Say whose answers to read (userId); the website always reads the signed-in member's.");
        }

        return Results.Ok(await MineAsync(db, eventId, subject, cancellationToken));
    }

    // ---- writes ----

    private static async Task<IResult> SetOne(
        long guildId, Guid eventId, Guid gameId, SetSignalRequest request, HttpContext context, TierResolver tiers,
        CalCronyDbContext db, IClock clock, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (request.Kind is { } kind && !Enum.IsDefined(kind))
        {
            return Bad("That answer is not one of: can teach, can play, new to it, not tonight.");
        }

        var ev = await FindVisibleEventAsync(db, context, tiers, guildId, eventId, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        if (Subject(context, request.UserId) is not { } subject)
        {
            return Bad("Say whose answer this is (userId); the website always means the signed-in member.");
        }

        if (!IsLive(ev))
        {
            return Results.Conflict(new ErrorResponse("That game night is over, so its answers are closed."));
        }

        var game = await db.GameNightGames.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameId && g.GuildId == guildId, cancellationToken);
        if (game is null || (!game.Active && request.Kind is not null))
        {
            return Bad("Choose a game from this server's library (retired games can't be answered).");
        }

        await using (var transaction = await EventPostThrottle.LockNightAsync(db, ev.Id, cancellationToken))
        {
            await ApplyAsync(db, eventId, subject, [new SignalDto(gameId, request.Kind ?? default)], clearOnly: request.Kind is null, replaceAll: false, cancellationToken);
            await EventPostThrottle.EnqueueAsync(db, ev, clock, Interval(configuration), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.Ok(await MineAsync(db, eventId, subject, cancellationToken));
    }

    private static async Task<IResult> SetMine(
        long guildId, Guid eventId, SetMySignalsRequest request, HttpContext context, TierResolver tiers, CalCronyDbContext db,
        IClock clock, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var ev = await FindVisibleEventAsync(db, context, tiers, guildId, eventId, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        if (Subject(context, request.UserId) is not { } subject)
        {
            return Bad("Say whose answers these are (userId); the website always means the signed-in member.");
        }

        if (!IsLive(ev))
        {
            return Results.Conflict(new ErrorResponse("That game night is over, so its answers are closed."));
        }

        var wanted = (request.Signals ?? []).GroupBy(s => s.GameId).Select(g => g.Last()).ToList();
        if (wanted.Any(s => !Enum.IsDefined(s.Kind)))
        {
            return Bad("That answer is not one of: can teach, can play, new to it, not tonight.");
        }

        var activeIds = (await ActiveGamesAsync(db, guildId, cancellationToken)).Select(g => g.Id).ToHashSet();
        if (wanted.Any(s => !activeIds.Contains(s.GameId)))
        {
            return Bad("Every game must be one in this server's library (retired games can't be answered).");
        }

        await using (var transaction = await EventPostThrottle.LockNightAsync(db, ev.Id, cancellationToken))
        {
            await ApplyAsync(db, eventId, subject, wanted, clearOnly: false, replaceAll: true, cancellationToken);
            await EventPostThrottle.EnqueueAsync(db, ev, clock, Interval(configuration), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.Ok(await MineAsync(db, eventId, subject, cancellationToken));
    }

    // ---- helpers ----

    /// <summary>Sets, replaces or clears a member's answers. A replace removes whatever is not listed; a
    /// single change leaves the rest alone. Changing an answer updates the row, so there is never more than one
    /// answer per member per game.</summary>
    private static async Task ApplyAsync(
        CalCronyDbContext db, Guid eventId, long userId, IReadOnlyList<SignalDto> signals, bool clearOnly, bool replaceAll,
        CancellationToken cancellationToken)
    {
        var mine = await db.GameNightSignals.Where(s => s.EventId == eventId && s.UserId == userId).ToListAsync(cancellationToken);
        if (replaceAll)
        {
            var keep = signals.Select(s => s.GameId).ToHashSet();
            db.GameNightSignals.RemoveRange(mine.Where(s => !keep.Contains(s.GameId)));
        }

        foreach (var signal in signals)
        {
            var existing = mine.FirstOrDefault(s => s.GameId == signal.GameId);
            if (clearOnly)
            {
                if (existing is not null)
                {
                    db.GameNightSignals.Remove(existing);
                }

                continue;
            }

            if (existing is null)
            {
                db.GameNightSignals.Add(new GameNightSignal { EventId = eventId, UserId = userId, GameId = signal.GameId, Kind = signal.Kind });
            }
            else
            {
                existing.Kind = signal.Kind;
            }
        }
    }

    /// <summary>The member an answer is about: the signed-in member for the website, the stated one for the bot.</summary>
    private static long? Subject(HttpContext context, long? stated) =>
        context.User.IsBot() ? stated is > 0 ? stated : null : context.User.WebUserId();

    private static async Task<Event?> FindVisibleEventAsync(
        CalCronyDbContext db, HttpContext context, TierResolver tiers, long guildId, Guid eventId, CancellationToken cancellationToken)
    {
        var ev = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId && e.GuildId == guildId, cancellationToken);
        if (ev is null)
        {
            return null;
        }

        var tier = (await tiers.ResolveAsync(context.User, guildId, cancellationToken)).Tier;
        return await VisibilityFilter.CanSeeEventAsync(db, eventId, context.User.WebUserId() ?? 0, tier, cancellationToken) ? ev : null;
    }

    private static bool IsLive(Event ev) => ev.Status is EventStatus.Scheduled or EventStatus.Started;

    private static int Interval(IConfiguration configuration) =>
        Math.Clamp(configuration.GetValue(IntervalKey, DefaultIntervalSeconds), 0, 600);

    private static async Task<MySignalsDto> MineAsync(CalCronyDbContext db, Guid eventId, long userId, CancellationToken cancellationToken) =>
        new([.. (await db.GameNightSignals.AsNoTracking().Where(s => s.EventId == eventId && s.UserId == userId).ToListAsync(cancellationToken))
            .Select(s => new SignalDto(s.GameId, s.Kind))]);

    internal static async Task<List<(Guid Id, string Name)>> ActiveGamesAsync(CalCronyDbContext db, long guildId, CancellationToken cancellationToken) =>
        (await db.GameNightGames.AsNoTracking().Where(g => g.GuildId == guildId && g.Active).Select(g => new { g.Id, g.Name }).ToListAsync(cancellationToken))
            .Select(g => (g.Id, g.Name)).ToList();

    internal static async Task<List<PersonSignalDto>> PeopleAsync(CalCronyDbContext db, Guid eventId, CancellationToken cancellationToken) =>
        (await db.GameNightSignals.AsNoTracking().Where(s => s.EventId == eventId).ToListAsync(cancellationToken))
            .Select(s => new PersonSignalDto(s.UserId, s.GameId, s.Kind)).ToList();

    private static IResult Bad(string message) => Results.BadRequest(new ErrorResponse(message));
}

/// <summary>Adds a night's answers up (see <see cref="SignalMath"/>) and attaches the result to the event DTO the
/// bot renders the post from.</summary>
public static class NightSummaries
{
    /// <summary>The summary for one night.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="eventId">The night.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The summary (every active game appears, even with no answers).</returns>
    public static async Task<NightSignalsSummary> SummarizeAsync(CalCronyDbContext db, long guildId, Guid eventId, CancellationToken cancellationToken) =>
        SignalMath.Summarize(
            await SignalEndpoints.ActiveGamesAsync(db, guildId, cancellationToken),
            await SignalEndpoints.PeopleAsync(db, eventId, cancellationToken));

    /// <summary>Returns the event DTO with the night's summary attached, or the DTO unchanged when the server has no
    /// active games (nothing to pick, so the post stays as it was). One call from the core event endpoints.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="dto">The event DTO.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The DTO, with <see cref="EventDto.GameNights"/> set when there are games.</returns>
    public static async Task<EventDto> AttachAsync(CalCronyDbContext db, EventDto dto, CancellationToken cancellationToken)
    {
        var games = await SignalEndpoints.ActiveGamesAsync(db, dto.GuildId, cancellationToken);
        if (games.Count == 0)
        {
            return dto;
        }

        var people = await SignalEndpoints.PeopleAsync(db, dto.Id, cancellationToken);
        return dto with { GameNights = SignalMath.Summarize(games, people) };
    }
}

/// <summary>Holds back re-renders of an event post so a burst of changes becomes one edit.
/// <para>A change queues a <see cref="DeliveryType.SyncEventMessage"/> due one interval from now, unless one is already
/// waiting that the bot has not been served yet (<c>Attempts == 0</c>): that one will read the data fresh when it runs,
/// so it already covers the change. A row the bot has picked up may have read the data before the change, so it does not
/// count. The result: at most one edit per interval, and the last change in a window is always shown.</para></summary>
public static class EventPostThrottle
{
    /// <summary>Starts a transaction that holds a per-night lock until it commits, so answers to one night are applied one at a time
    /// and the "is an edit already waiting?" check in <see cref="EnqueueAsync"/> cannot race: without it, a few hundred answers
    /// arriving together could each see no waiting edit and each queue their own. The lock is a Postgres advisory transaction
    /// lock keyed on the event, so different nights never wait for each other and it is released on commit or rollback.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="eventId">The night.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The open transaction; commit it after saving.</returns>
    public static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> LockNightAsync(
        CalCronyDbContext db, Guid eventId, CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var key = eventId.ToString();
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
        return transaction;
    }

    /// <summary>Queues (or coalesces into) the delayed re-render for an event. Adds to the context; the caller saves.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="intervalSeconds">How long to hold the re-render back.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>True when a new row was queued; false when one was already waiting or the event has no post.</returns>
    public static async Task<bool> EnqueueAsync(
        CalCronyDbContext db, Event ev, IClock clock, int intervalSeconds, CancellationToken cancellationToken)
    {
        if (ev.MessageId is null)
        {
            return false;
        }

        var payloadJson = JsonSerializer.Serialize(new SyncEventMessagePayload(ev.Id));
        if (await db.Deliveries.AnyAsync(
                d => d.Type == DeliveryType.SyncEventMessage && d.Status == DeliveryStatus.Pending
                     && d.Attempts == 0 && d.PayloadJson == payloadJson,
                cancellationToken))
        {
            return false;
        }

        var now = clock.GetCurrentInstant();
        db.Deliveries.Add(new Delivery
        {
            Id = Guid.NewGuid(),
            Type = DeliveryType.SyncEventMessage,
            ChannelId = ev.ChannelId,
            PayloadJson = payloadJson,
            DueAt = now.Plus(Duration.FromSeconds(intervalSeconds)),
            Status = DeliveryStatus.Pending,
            CreatedAt = now,
        });
        return true;
    }
}
