using CalCrony.Api.Data;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.GameNights;

/// <summary>How people are named: the bot's "First L" snapshots for the members who take part (it pushes them, the
/// API never reads Discord), and guests, whose typed name is the only personal detail Game Nights keeps and can be
/// erased on request. Everything shown comes through <see cref="NameService"/>.</summary>
public static class NameEndpoints
{
    /// <summary>The most names one batch may carry.</summary>
    public const int MaxBatch = 500;

    /// <summary>The most members one "wanted" answer lists.</summary>
    public const int MaxWanted = 200;

    /// <summary>The most guests a server may keep.</summary>
    public const int MaxGuests = 500;

    /// <summary>Maps the name and guest routes onto the Game Nights group.</summary>
    /// <param name="g">The <c>/guilds/{guildId}/game-nights</c> group.</param>
    public static void MapNameEndpoints(this RouteGroupBuilder g)
    {
        g.MapPut("/member-names", PutNames).RequireAuthorization("BotOnly").RequireTier(GameNightsTier.Member);
        g.MapGet("/member-names/wanted", GetWanted).RequireAuthorization("BotOnly").RequireTier(GameNightsTier.Member);
        g.MapGet("/names", GetNames).RequireTier(GameNightsTier.Member);

        g.MapGet("/guests", ListGuests).RequireTier(GameNightsTier.Organiser);
        g.MapPost("/guests", CreateGuest).RequireTier(GameNightsTier.Organiser);
        g.MapDelete("/guests/{guestId:guid}", EraseGuest).RequireTier(GameNightsTier.Moderator);
    }

    // ---- the bot's snapshots ----

    private static async Task<IResult> PutNames(
        long guildId, PutMemberNamesRequest request, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        if (request.Members is null || request.Members.Count > MaxBatch)
        {
            return Bad($"Send up to {MaxBatch} names at a time.");
        }

        // Only the derived "First L" may be stored. Anything else (a raw nickname, a username) is refused, so the
        // API cannot end up holding it however the bot is changed.
        if (request.Members.Any(m => m.UserId <= 0 || (m.DisplayName is not null && DisplayNames.FromNickname(m.DisplayName) != m.DisplayName)))
        {
            return Bad("Names must be in the First L form (for example \"Alex P\"), or null when the nickname doesn't follow it.");
        }

        var ids = request.Members.Select(m => m.UserId).Distinct().ToList();
        var existing = await db.GameNightMemberNames.Where(n => n.GuildId == guildId && ids.Contains(n.UserId)).ToDictionaryAsync(n => n.UserId, cancellationToken);
        var now = clock.GetCurrentInstant();
        foreach (var member in request.Members.GroupBy(m => m.UserId).Select(x => x.Last()))
        {
            if (!existing.TryGetValue(member.UserId, out var row))
            {
                row = new GameNightMemberName { GuildId = guildId, UserId = member.UserId };
                db.GameNightMemberNames.Add(row);
            }

            row.Left = member.Left;
            row.DisplayName = member.Left ? null : member.DisplayName;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Who the bot should look up: members at tables, in results or answering for nights in the last 90 days
    /// or later, whose name is missing or stale (a matched one after 12 hours, an unmatched one after an hour, so a
    /// fixed nickname is picked up quickly). Members who have left are not asked about again.</summary>
    private static async Task<IResult> GetWanted(long guildId, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var since = now.Minus(Duration.FromDays(90));

        var seated = db.GameNightTableSeats
            .Where(s => s.UserId != null && db.GameNightTables.Any(t => t.Id == s.TableId && t.Status != TableStatus.Cancelled
                && db.Events.Any(e => e.Id == t.EventId && e.GuildId == guildId && e.StartsAt >= since)))
            .Select(s => s.UserId!.Value);
        var reported = db.GameNightResultPlayers
            .Where(p => p.UserId != null && db.GameNightResults.Any(r => r.Id == p.ResultId
                && db.GameNightTables.Any(t => t.Id == r.TableId && db.Events.Any(e => e.Id == t.EventId && e.GuildId == guildId))))
            .Select(p => p.UserId!.Value);
        var answered = db.GameNightSignals
            .Where(s => db.Events.Any(e => e.Id == s.EventId && e.GuildId == guildId && e.StartsAt >= since))
            .Select(s => s.UserId);

        var candidates = await seated.Union(reported).Union(answered).ToListAsync(cancellationToken);
        var rows = await db.GameNightMemberNames.AsNoTracking()
            .Where(n => n.GuildId == guildId && candidates.Contains(n.UserId)).ToDictionaryAsync(n => n.UserId, cancellationToken);

        var wanted = candidates.Where(id =>
        {
            if (!rows.TryGetValue(id, out var row))
            {
                return true;
            }

            var age = (now - row.UpdatedAt).ToTimeSpan();
            return !row.Left && age > (row.DisplayName is null ? NameService.UnmatchedMaxAge : NameService.MatchedMaxAge);
        }).Take(MaxWanted).ToList();
        return Results.Ok(new WantedNamesResponse(wanted));
    }

    // ---- reading names ----

    private static async Task<IResult> GetNames(long guildId, string? ids, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var wanted = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => long.TryParse(part, out var id) && id > 0 ? id : (long?)null)
            .Where(id => id is not null).Select(id => id!.Value).Distinct().Take(100).ToList();
        var names = await NameService.ResolveAsync(db, guildId, wanted, null, cancellationToken);
        return Results.Ok(names.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.DisplayName));
    }

    // ---- guests ----

    private static async Task<IResult> ListGuests(long guildId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var guests = await db.GameNightGuests.AsNoTracking()
            .Where(g => g.GuildId == guildId && !g.Erased).OrderBy(g => g.DisplayName).ToListAsync(cancellationToken);
        return Results.Ok(guests.Select(g => new GuestDto(g.Id, g.DisplayName)).ToList());
    }

    private static async Task<IResult> CreateGuest(
        long guildId, CreateGuestRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (DisplayNames.Tidy(request.DisplayName) is not { } name)
        {
            return Bad("A guest's name is given as First L (for example \"Sam T\").");
        }

        if (await db.GameNightGuests.CountAsync(g => g.GuildId == guildId && !g.Erased, cancellationToken) >= MaxGuests)
        {
            return Bad($"A server can keep at most {MaxGuests} guests. Erase ones you no longer need.");
        }

        var guest = new GameNightGuest { Id = Guid.NewGuid(), GuildId = guildId, DisplayName = name };
        db.GameNightGuests.Add(guest);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/guilds/{guildId}/game-nights/guests/{guest.Id}", new GuestDto(guest.Id, guest.DisplayName));
    }

    /// <summary>Erases a guest's name on request. The row stays (results and seats keep their shape) but the name is
    /// replaced by "Former guest".</summary>
    private static async Task<IResult> EraseGuest(long guildId, Guid guestId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var guest = await db.GameNightGuests.FirstOrDefaultAsync(g => g.Id == guestId && g.GuildId == guildId, cancellationToken);
        if (guest is null)
        {
            return Results.NotFound();
        }

        guest.DisplayName = DisplayNames.FormerGuest;
        guest.Erased = true;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static IResult Bad(string message) => Results.BadRequest(new ErrorResponse(message));
}
