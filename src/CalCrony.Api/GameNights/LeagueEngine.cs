using CalCrony.Api.Data;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.GameNights;

/// <summary>Builds a game's league ratings from its confirmed results.
/// <para><b>What counts.</b> A result counts when it is confirmed, its game has ratings switched on, its table is public and the
/// night it was played on is public. Nothing else ever reaches a league: league ratings are kept indefinitely and shown to
/// everyone, so a private table must never feed them (the database also refuses it: see the <c>InLeague</c> check constraints).</para>
/// <para><b>How.</b> Ratings are always rebuilt by replaying every counted result of the game in order (night, then when it was
/// reported) through the rating scheme, so there is one way to get a rating and correcting history is just a rebuild: voiding or
/// replacing a confirmed result, ending a season, or re-ordering nights all call this and the numbers follow. A player's first
/// counted result creates their rating at the game's start rating (players join by playing). Each result is rated twice: into
/// the all-time rating, and into the season its night falls in, if there is one; seasons start everyone fresh.</para>
/// <para>Run it inside the caller's transaction, after the change that triggered it has been saved.</para></summary>
public static class LeagueEngine
{
    /// <summary>The rating scheme leagues use. One place, so swapping it later is a one-line change.</summary>
    public static readonly IRatingScheme Scheme = new PairwiseElo();

    /// <summary>Rebuilds one game's league.</summary>
    /// <param name="db">The database context (transaction open; the triggering change already saved).</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="gameId">The game.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task RecomputeAsync(
        CalCronyDbContext db, long guildId, Guid gameId, IClock clock, CancellationToken cancellationToken)
    {
        // One rebuild of a game at a time.
        await db.Database.ExecuteSqlAsync($"""SELECT "Id" FROM "GameNightGames" WHERE "Id" = {gameId} FOR UPDATE""", cancellationToken);
        var game = await db.GameNightGames.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameId && g.GuildId == guildId, cancellationToken);
        if (game is null)
        {
            return;
        }

        var counted = game.RatingEnabled ? await CountedResultsAsync(db, guildId, gameId, cancellationToken) : [];
        var league = await db.GameNightLeagues.FirstOrDefaultAsync(l => l.GameId == gameId, cancellationToken);
        if (league is null && counted.Count > 0)
        {
            league = new GameNightLeague { Id = Guid.NewGuid(), GuildId = guildId, GameId = gameId, CreatedAt = clock.GetCurrentInstant() };
            db.GameNightLeagues.Add(league);
            await db.SaveChangesAsync(cancellationToken);
        }

        if (league is not null)
        {
            await db.GameNightRatingEvents.Where(e => e.LeagueId == league.Id).ExecuteDeleteAsync(cancellationToken);
            await db.GameNightPlayerRatings.Where(r => r.LeagueId == league.Id).ExecuteDeleteAsync(cancellationToken);
            await ReplayAsync(db, league, game, counted, cancellationToken);
        }

        await FlagAsync(db, gameId, counted, cancellationToken);
    }

    /// <summary>The confirmed, rated, public results of a game, in the order they are replayed: by night, then by when
    /// each was reported.</summary>
    private static async Task<List<Counted>> CountedResultsAsync(
        CalCronyDbContext db, long guildId, Guid gameId, CancellationToken cancellationToken)
    {
        var rows = await (
            from r in db.GameNightResults.AsNoTracking()
            join t in db.GameNightTables.AsNoTracking() on r.TableId equals t.Id
            join e in db.Events.AsNoTracking() on t.EventId equals e.Id
            where r.Status == ResultStatus.Confirmed
                  && t.GameId == gameId
                  && t.Visibility == GameNightsVisibility.Public
                  && e.GuildId == guildId
                  && !db.GameNightEventExtensions.Any(x => x.EventId == e.Id && x.Visibility != GameNightsVisibility.Public)
            orderby e.StartsAt, r.SubmittedAt, r.Id
            select new { r.Id, r.TableId, EventId = e.Id, PlayedAt = e.StartsAt })
            .ToListAsync(cancellationToken);

        var ids = rows.Select(r => r.Id).ToList();
        var players = (await db.GameNightResultPlayers.AsNoTracking().Where(p => ids.Contains(p.ResultId)).ToListAsync(cancellationToken))
            .ToLookup(p => p.ResultId);
        return [.. rows.Select(r => new Counted(r.Id, r.TableId, r.EventId, r.PlayedAt, [.. players[r.Id]]))];
    }

    private static async Task ReplayAsync(
        CalCronyDbContext db, GameNightLeague league, GameNightGame game, List<Counted> counted, CancellationToken cancellationToken)
    {
        var seasons = await db.GameNightSeasons.AsNoTracking().Where(s => s.LeagueId == league.Id).OrderBy(s => s.StartsAt).ToListAsync(cancellationToken);
        var state = new Dictionary<(Guid? Season, string Key), Track>();

        foreach (var result in counted)
        {
            var ordered = result.Players.OrderBy(p => p.UserId ?? long.MaxValue).ThenBy(p => p.GuestId).ToList();
            var placements = RatingPlacements.For(game.ResultType, [.. ordered.Select(p => p.Placement)]);
            var season = seasons.FirstOrDefault(s => result.PlayedAt >= s.StartsAt && (s.EndsAt is null || result.PlayedAt < s.EndsAt));

            foreach (var seasonId in season is null ? new Guid?[] { null } : [null, season.Id])
            {
                var rated = ordered.Select((p, i) => new RatedPlayer(
                    KeyOf(p), state.TryGetValue((seasonId, KeyOf(p)), out var known) ? known.Rating : game.StartRating, placements[i])).ToList();
                var changes = Scheme.Apply(rated, game.RatingK);
                for (var i = 0; i < ordered.Count; i++)
                {
                    var player = ordered[i];
                    var current = state.TryGetValue((seasonId, KeyOf(player)), out var existing) ? existing : new Track(player.UserId, player.GuestId, game.StartRating, 0);
                    state[(seasonId, KeyOf(player))] = current with { Rating = changes[i].After, Games = current.Games + 1 };
                    db.GameNightRatingEvents.Add(new GameNightRatingEvent
                    {
                        Id = Guid.NewGuid(), LeagueId = league.Id, SeasonId = seasonId, ResultId = result.Id,
                        UserId = player.UserId, GuestId = player.GuestId, Placement = player.Placement,
                        Before = changes[i].Before, After = changes[i].After, PlayedAt = result.PlayedAt,
                    });
                }
            }
        }

        foreach (var ((seasonId, _), track) in state)
        {
            db.GameNightPlayerRatings.Add(new GameNightPlayerRating
            {
                Id = Guid.NewGuid(), LeagueId = league.Id, SeasonId = seasonId, UserId = track.UserId, GuestId = track.GuestId,
                Rating = track.Rating, GamesPlayed = track.Games,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Marks which tables and nights currently feed a league. The flags exist so the database can refuse to make a
    /// table or night private while it does (a check constraint), whatever any code path does.</summary>
    private static async Task FlagAsync(CalCronyDbContext db, Guid gameId, List<Counted> counted, CancellationToken cancellationToken)
    {
        var countedTables = counted.Select(c => c.TableId).ToHashSet();
        var tables = await db.GameNightTables.Where(t => t.GameId == gameId && (t.InLeague || countedTables.Contains(t.Id))).ToListAsync(cancellationToken);
        foreach (var table in tables)
        {
            table.InLeague = countedTables.Contains(table.Id);
        }

        await db.SaveChangesAsync(cancellationToken);

        // A night is in a league while any of its tables (of any game) is.
        var events = tables.Select(t => t.EventId).Distinct().ToList();
        var extensions = await db.GameNightEventExtensions.Where(x => events.Contains(x.EventId)).ToDictionaryAsync(x => x.EventId, cancellationToken);
        foreach (var eventId in events)
        {
            var inLeague = await db.GameNightTables.AsNoTracking().AnyAsync(t => t.EventId == eventId && t.InLeague, cancellationToken);
            if (!extensions.TryGetValue(eventId, out var extension))
            {
                if (!inLeague)
                {
                    continue;
                }

                extension = new GameNightEventExtension { EventId = eventId };
                db.GameNightEventExtensions.Add(extension);
            }

            extension.InLeague = inLeague;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string KeyOf(GameNightResultPlayer player) => player.UserId is { } user ? $"u{user}" : $"g{player.GuestId}";

    private sealed record Counted(Guid Id, Guid TableId, Guid EventId, Instant PlayedAt, IReadOnlyList<GameNightResultPlayer> Players);

    private sealed record Track(long? UserId, Guid? GuestId, double Rating, int Games);
}
