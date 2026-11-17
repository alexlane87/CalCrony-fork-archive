using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.GameNights;

/// <summary>Leagues: a table of ratings per game (all-time, or per season), a player's rating history, and the seasons.
/// Everything here is read from ratings that <see cref="LeagueEngine"/> builds from confirmed results of public tables, so the
/// tables are safe to show to every member.</summary>
public static class LeagueEndpoints
{
    /// <summary>The most seasons one league may have.</summary>
    public const int MaxSeasons = 100;

    /// <summary>Maps the league routes onto the Game Nights group.</summary>
    /// <param name="g">The <c>/guilds/{guildId}/game-nights</c> group.</param>
    public static void MapLeagueEndpoints(this RouteGroupBuilder g)
    {
        g.MapGet("/games/{gameId:guid}/league", GetStandings).RequireTier(GameNightsTier.Member);
        g.MapGet("/games/{gameId:guid}/league/history", GetHistory).RequireTier(GameNightsTier.Member);
        g.MapGet("/leagues/me", GetMine).RequireTier(GameNightsTier.Member);
        g.MapPost("/games/{gameId:guid}/league/seasons", CreateSeason).RequireTier(GameNightsTier.Organiser);
        g.MapPost("/games/{gameId:guid}/league/seasons/{seasonId:guid}/end", EndSeason).RequireTier(GameNightsTier.Organiser);
        g.MapPost("/games/{gameId:guid}/league/recompute", Recompute).RequireTier(GameNightsTier.Moderator);
    }

    // ---- reads ----

    private static async Task<IResult> GetStandings(
        long guildId, Guid gameId, Guid? seasonId, bool? allTime, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var game = await db.GameNightGames.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameId && g.GuildId == guildId, cancellationToken);
        if (game is null)
        {
            return Results.NotFound();
        }

        var league = await db.GameNightLeagues.AsNoTracking().FirstOrDefaultAsync(l => l.GameId == gameId, cancellationToken);
        var seasons = league is null
            ? []
            : await db.GameNightSeasons.AsNoTracking().Where(s => s.LeagueId == league.Id).OrderByDescending(s => s.StartsAt).ToListAsync(cancellationToken);
        var scope = ChooseScope(seasons, seasonId, allTime == true, clock.GetCurrentInstant());
        if (scope is null)
        {
            return Results.NotFound(); // a season that is not this league's
        }

        var seasonDtos = seasons.Select(ToDto).ToList();
        if (league is null)
        {
            return Results.Ok(new LeagueStandingsDto(game.Id, game.Name, null, scope.Value.Label, [], seasonDtos, 0));
        }

        var rows = await StandingsAsync(db, guildId, league.Id, scope.Value.SeasonId, cancellationToken);
        var played = await db.GameNightRatingEvents.AsNoTracking()
            .Where(e => e.LeagueId == league.Id && e.SeasonId == scope.Value.SeasonId).Select(e => e.ResultId).Distinct().CountAsync(cancellationToken);
        return Results.Ok(new LeagueStandingsDto(game.Id, game.Name, scope.Value.SeasonId, scope.Value.Label, rows, seasonDtos, played));
    }

    private static async Task<IResult> GetHistory(
        long guildId, Guid gameId, long? userId, Guid? guestId, Guid? seasonId, bool? allTime, HttpContext context, CalCronyDbContext db,
        IClock clock, CancellationToken cancellationToken)
    {
        var subject = guestId is null ? (context.User.IsBot() ? userId : userId ?? context.User.WebUserId()) : null;
        if (subject is null && guestId is null)
        {
            return Bad("Say whose history to read (userId or guestId).");
        }

        var league = await db.GameNightLeagues.AsNoTracking().FirstOrDefaultAsync(l => l.GameId == gameId && l.GuildId == guildId, cancellationToken);
        if (league is null)
        {
            return Results.Ok(new List<RatingHistoryEntryDto>());
        }

        var seasons = await db.GameNightSeasons.AsNoTracking().Where(s => s.LeagueId == league.Id).ToListAsync(cancellationToken);
        var scope = ChooseScope(seasons, seasonId, allTime == true, clock.GetCurrentInstant());
        if (scope is null)
        {
            return Results.NotFound();
        }

        // Same order the engine replays in (night, then when it was reported, then id), so two games at one night stay in the order
        // they were played instead of falling back to a random event id.
        var events = await (
            from e in db.GameNightRatingEvents.AsNoTracking()
            join r in db.GameNightResults.AsNoTracking() on e.ResultId equals r.Id
            where e.LeagueId == league.Id && e.SeasonId == scope.Value.SeasonId && e.UserId == subject && e.GuestId == guestId
            orderby e.PlayedAt, r.SubmittedAt, r.Id
            select e)
            .ToListAsync(cancellationToken);
        return Results.Ok(events.Select(e => new RatingHistoryEntryDto(e.ResultId, e.PlayedAt.ToDateTimeOffset(), e.Placement, e.Before, e.After)).ToList());
    }

    private static async Task<IResult> GetMine(
        long guildId, long? userId, HttpContext context, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var subject = context.User.IsBot() ? userId : userId ?? context.User.WebUserId();
        if (subject is null)
        {
            return Bad("Say whose ratings to read (userId).");
        }

        var now = clock.GetCurrentInstant();
        var leagues = await db.GameNightLeagues.AsNoTracking().Where(l => l.GuildId == guildId).ToListAsync(cancellationToken);
        var names = await db.GameNightGames.AsNoTracking().Where(g => g.GuildId == guildId).ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        var mine = new List<MyRatingDto>();
        foreach (var league in leagues)
        {
            var seasons = await db.GameNightSeasons.AsNoTracking().Where(s => s.LeagueId == league.Id).ToListAsync(cancellationToken);
            var scope = ChooseScope(seasons, null, false, now)!.Value;
            var ratings = await db.GameNightPlayerRatings.AsNoTracking().Where(r => r.LeagueId == league.Id && r.SeasonId == scope.SeasonId).ToListAsync(cancellationToken);
            var own = ratings.FirstOrDefault(r => r.UserId == subject);
            if (own is null)
            {
                continue;
            }

            var shown = RatingPlacements.Display(own.Rating);
            mine.Add(new MyRatingDto(
                league.GameId, names.GetValueOrDefault(league.GameId, "Game"), scope.Label, shown,
                1 + ratings.Count(r => RatingPlacements.Display(r.Rating) > shown), ratings.Count, own.GamesPlayed));
        }

        return Results.Ok(mine.OrderBy(m => m.GameName).ToList());
    }

    // ---- seasons ----

    private static async Task<IResult> CreateSeason(
        long guildId, Guid gameId, CreateSeasonRequest request, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > GameNightsModel.SeasonNameLength)
        {
            return Bad($"A season needs a name of up to {GameNightsModel.SeasonNameLength} characters.");
        }

        var now = clock.GetCurrentInstant();
        var starts = request.StartsAtUtc is { } given ? Instant.FromDateTimeOffset(given) : now;
        Instant? ends = request.EndsAtUtc is { } end ? Instant.FromDateTimeOffset(end) : null;
        if (ends is { } e && e <= starts)
        {
            return Bad("A season has to end after it starts.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"""SELECT "Id" FROM "GameNightGames" WHERE "Id" = {gameId} FOR UPDATE""", cancellationToken);
        var game = await db.GameNightGames.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameId && g.GuildId == guildId, cancellationToken);
        if (game is null)
        {
            return Results.NotFound();
        }

        var league = await db.GameNightLeagues.FirstOrDefaultAsync(l => l.GameId == gameId, cancellationToken);
        if (league is null)
        {
            league = new GameNightLeague { Id = Guid.NewGuid(), GuildId = guildId, GameId = gameId, CreatedAt = now };
            db.GameNightLeagues.Add(league);
            await db.SaveChangesAsync(cancellationToken);
        }

        var seasons = await db.GameNightSeasons.Where(s => s.LeagueId == league.Id).ToListAsync(cancellationToken);
        if (seasons.Count >= MaxSeasons)
        {
            return Bad($"A league can have at most {MaxSeasons} seasons.");
        }

        // Starting a season ends the one that is running when it starts. A season that has not begun by then, or that
        // starts at the same moment, is an overlap, not something to quietly rewrite.
        foreach (var running in seasons.Where(s => s.StartsAt < starts && (s.EndsAt is null || s.EndsAt > starts)))
        {
            running.EndsAt = starts;
        }

        var limit = ends ?? Instant.MaxValue;
        if (seasons.Any(s => s.StartsAt < limit && (s.EndsAt ?? Instant.MaxValue) > starts))
        {
            return Results.Conflict(new ErrorResponse("That would overlap another season of this league."));
        }

        var season = new GameNightSeason { Id = Guid.NewGuid(), LeagueId = league.Id, Name = name, StartsAt = starts, EndsAt = ends };
        db.GameNightSeasons.Add(season);
        await db.SaveChangesAsync(cancellationToken);
        await LeagueEngine.RecomputeAsync(db, guildId, gameId, clock, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Created($"/guilds/{guildId}/game-nights/games/{gameId}/league", ToDto(season));
    }

    private static async Task<IResult> EndSeason(
        long guildId, Guid gameId, Guid seasonId, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"""SELECT "Id" FROM "GameNightGames" WHERE "Id" = {gameId} FOR UPDATE""", cancellationToken);
        var season = await db.GameNightSeasons
            .Where(s => s.Id == seasonId && db.GameNightLeagues.Any(l => l.Id == s.LeagueId && l.GameId == gameId && l.GuildId == guildId))
            .FirstOrDefaultAsync(cancellationToken);
        if (season is null)
        {
            return Results.NotFound();
        }

        var now = clock.GetCurrentInstant();
        if (season.EndsAt is { } already && already <= now)
        {
            return Results.Conflict(new ErrorResponse("That season has already ended."));
        }

        season.EndsAt = now > season.StartsAt ? now : season.StartsAt.Plus(Duration.FromSeconds(1));
        await db.SaveChangesAsync(cancellationToken);
        await LeagueEngine.RecomputeAsync(db, guildId, gameId, clock, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(ToDto(season));
    }

    private static async Task<IResult> Recompute(
        long guildId, Guid gameId, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        if (!await db.GameNightGames.AnyAsync(g => g.Id == gameId && g.GuildId == guildId, cancellationToken))
        {
            return Results.NotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LeagueEngine.RecomputeAsync(db, guildId, gameId, clock, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    // ---- helpers ----

    /// <summary>Which table to show: a named season, the all-time table, or by default the running season if there is one.</summary>
    private static (Guid? SeasonId, string Label)? ChooseScope(IReadOnlyList<GameNightSeason> seasons, Guid? seasonId, bool allTime, Instant now)
    {
        if (seasonId is { } wanted)
        {
            return seasons.FirstOrDefault(s => s.Id == wanted) is { } named ? (named.Id, named.Name) : null;
        }

        if (allTime)
        {
            return (null, "All time");
        }

        var current = seasons.FirstOrDefault(s => s.StartsAt <= now && (s.EndsAt is null || s.EndsAt > now));
        return current is null ? (null, "All time") : (current.Id, current.Name);
    }

    private static async Task<IReadOnlyList<StandingDto>> StandingsAsync(
        CalCronyDbContext db, long guildId, Guid leagueId, Guid? seasonId, CancellationToken cancellationToken)
    {
        var ratings = await db.GameNightPlayerRatings.AsNoTracking().Where(r => r.LeagueId == leagueId && r.SeasonId == seasonId).ToListAsync(cancellationToken);
        var members = ratings.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).ToList();
        var names = await NameService.ResolveAsync(db, guildId, members, null, cancellationToken);
        var guestIds = ratings.Where(r => r.GuestId is not null).Select(r => r.GuestId!.Value).ToList();
        var guests = await db.GameNightGuests.AsNoTracking().Where(g => guestIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.DisplayName, cancellationToken);

        var sorted = ratings
            .Select(r => (Row: r, Name: r.UserId is { } u ? names[u].DisplayName : guests.GetValueOrDefault(r.GuestId!.Value, DisplayNames.FormerGuest)))
            .OrderByDescending(x => RatingPlacements.Display(x.Row.Rating)).ThenByDescending(x => x.Row.GamesPlayed).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rows = new List<StandingDto>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var shown = RatingPlacements.Display(sorted[i].Row.Rating);
            var rank = i > 0 && RatingPlacements.Display(sorted[i - 1].Row.Rating) == shown ? rows[i - 1].Rank : i + 1;
            rows.Add(new StandingDto(rank, sorted[i].Row.UserId, sorted[i].Row.GuestId, sorted[i].Name, shown, sorted[i].Row.Rating, sorted[i].Row.GamesPlayed));
        }

        return rows;
    }

    private static SeasonDto ToDto(GameNightSeason s) => new(s.Id, s.Name, s.StartsAt.ToDateTimeOffset(), s.EndsAt?.ToDateTimeOffset());

    private static IResult Bad(string message) => Results.BadRequest(new ErrorResponse(message));
}
