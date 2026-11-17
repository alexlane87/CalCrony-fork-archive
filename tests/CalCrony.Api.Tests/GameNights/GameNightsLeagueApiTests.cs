using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.GameNights;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests.GameNights;

/// <summary>The rating arithmetic against hand-worked examples (no database).</summary>
public class PairwiseEloTests
{
    private static readonly PairwiseElo Elo = new();

    private static RatedPlayer P(string key, double rating, int place) => new(key, rating, place);

    [Fact]
    public void Two_equal_players_move_by_half_the_k_factor_which_is_ordinary_elo()
    {
        var changes = Elo.Apply([P("a", 1000, 1), P("b", 1000, 2)], 32);

        Assert.Equal(16, changes[0].Delta, 9);
        Assert.Equal(-16, changes[1].Delta, 9);
        Assert.Equal((1000d, 1016d), (changes[0].Before, changes[0].After));
    }

    [Fact]
    public void An_upset_pays_more_and_matches_the_standard_elo_formula()
    {
        // 1000 beats 1200: E = 1 / (1 + 10^((1200-1000)/400)) = 0.2402530733..., delta = 32 * (1 - E) = 24.3119...
        var changes = Elo.Apply([P("low", 1000, 1), P("high", 1200, 2)], 32);

        var expected = 32 * (1 - 1 / (1 + Math.Pow(10, 200.0 / 400)));
        Assert.Equal(expected, changes[0].Delta, 9);
        Assert.Equal(24.3119, changes[0].Delta, 3);
        Assert.Equal(-expected, changes[1].Delta, 9);
    }

    [Fact]
    public void The_favourite_winning_gains_little()
    {
        var changes = Elo.Apply([P("high", 1200, 1), P("low", 1000, 2)], 32);

        Assert.Equal(32 * (1 / (1 + Math.Pow(10, 200.0 / 400))), changes[0].Delta, 9);
        Assert.Equal(7.6881, changes[0].Delta, 3);
    }

    [Fact]
    public void Four_equal_players_finishing_one_two_three_four_move_by_the_hand_worked_amounts_and_the_total_is_conserved()
    {
        // K/(N-1) = 32/3. First beats three (sum 1.5) = +16; second sum 0.5 = +5.3333; third -0.5 = -5.3333; fourth -1.5 = -16.
        var changes = Elo.Apply([P("1", 1000, 1), P("2", 1000, 2), P("3", 1000, 3), P("4", 1000, 4)], 32);

        Assert.Equal([16.0, 16.0 / 3, -16.0 / 3, -16.0], changes.Select(c => c.Delta).ToArray(), new ToleranceComparer());
        Assert.Equal(0, changes.Sum(c => c.Delta), 9);
        Assert.Equal(4000, changes.Sum(c => c.After), 9);
    }

    [Fact]
    public void Total_rating_is_conserved_whatever_the_ratings_and_placements()
    {
        var players = new[] { P("a", 1432, 2), P("b", 987, 1), P("c", 1105, 4), P("d", 1250, 3), P("e", 800, 5), P("f", 1500, 6) };

        var changes = Elo.Apply(players, 40);

        Assert.Equal(0, changes.Sum(c => c.Delta), 9);
    }

    [Fact]
    public void A_tie_between_equals_changes_nothing_and_between_unequals_favours_the_lower()
    {
        var equal = Elo.Apply([P("a", 1000, 1), P("b", 1000, 1)], 32);
        Assert.All(equal, c => Assert.Equal(0, c.Delta, 9));

        // 1000 ties 1200: S = 0.5, E(low) = 0.2402530733, delta = 32 * (0.5 - E) = 8.3119...
        var unequal = Elo.Apply([P("low", 1000, 1), P("high", 1200, 1)], 32);
        Assert.Equal(32 * (0.5 - 1 / (1 + Math.Pow(10, 0.5))), unequal[0].Delta, 9);
        Assert.Equal(8.3119, unequal[0].Delta, 3);
        Assert.Equal(-unequal[0].Delta, unequal[1].Delta, 9);
    }

    [Fact]
    public void A_three_way_tie_for_first_beats_the_last_and_ties_with_each_other()
    {
        var changes = Elo.Apply([P("a", 1000, 1), P("b", 1000, 1), P("c", 1000, 3)], 32);

        // a: tie b (0), beat c (+0.5) = 32/2 * 0.5 = +8; same for b; c: -0.5 - 0.5 = -1 -> -16.
        Assert.Equal([8.0, 8.0, -16.0], changes.Select(c => c.Delta).ToArray(), new ToleranceComparer());
    }

    [Fact]
    public void A_single_winner_game_rates_the_winner_against_everyone_and_the_rest_as_tied()
    {
        var placements = RatingPlacements.For(GameResultType.SingleWinner, [null, 1, null, null]);
        Assert.Equal([2, 1, 2, 2], placements);

        var changes = Elo.Apply([P("a", 1000, placements[0]), P("w", 1000, placements[1]), P("c", 1000, placements[2]), P("d", 1000, placements[3])], 32);

        // winner: 3 wins at 0.5 expected = 32/3 * 1.5 = +16; each other: lost once (-0.5), tied twice (0) = -5.3333.
        Assert.Equal(16, changes[1].Delta, 9);
        Assert.All(new[] { 0, 2, 3 }, i => Assert.Equal(-16.0 / 3, changes[i].Delta, 9));
        Assert.Equal(0, changes.Sum(c => c.Delta), 9);
    }

    [Fact]
    public void A_winning_side_game_ties_players_on_the_same_side()
    {
        var placements = RatingPlacements.For(GameResultType.WinningSide, [1, 1, 2, 2]);

        var changes = Elo.Apply([P("a", 1000, placements[0]), P("b", 1000, placements[1]), P("c", 1000, placements[2]), P("d", 1000, placements[3])], 32);

        Assert.Equal([32.0 / 3, 32.0 / 3, -32.0 / 3, -32.0 / 3], changes.Select(c => c.Delta).ToArray(), new ToleranceComparer());
    }

    [Fact]
    public void One_player_alone_does_not_move()
    {
        var changes = Elo.Apply([P("a", 1000, 1)], 32);

        Assert.Equal(0, changes[0].Delta);
    }

    [Theory]
    [InlineData(1015.5, 1016)]
    [InlineData(1015.49, 1015)]
    [InlineData(984.5, 985)]
    public void A_rating_is_shown_rounded_half_up(double exact, int shown) =>
        Assert.Equal(shown, RatingPlacements.Display(exact));

    private sealed class ToleranceComparer : IEqualityComparer<double[]>
    {
        public bool Equals(double[]? x, double[]? y) => x is not null && y is not null && x.Length == y.Length && x.Zip(y).All(p => Math.Abs(p.First - p.Second) < 1e-9);

        public int GetHashCode(double[] obj) => obj.Length;
    }
}

/// <summary>Leagues over HTTP: ratings from confirmed results of public tables, correcting history, seasons, the database
/// constraint, and the retention purge leaving league rows alone.</summary>
public class GameNightsLeagueApiTests(GameNightsFixture fixture) : IClassFixture<GameNightsFixture>
{
    private HttpClient Bot => fixture.Client;

    private static long nextGuild = 69000;

    private static long NewGuildId() => Interlocked.Increment(ref nextGuild);

    private sealed record Night(long Guild, HttpClient Admin, long A, HttpClient AClient, long B, HttpClient BClient, GameDto Root, Guid EventId);

    private static string Base(Night n) => $"/guilds/{n.Guild}/game-nights";

    private static double Expected(double ra, double rb, bool aWins, double k = 32)
    {
        var e = 1 / (1 + Math.Pow(10, (rb - ra) / 400));
        return ra + k * ((aWins ? 1 : 0) - e);
    }

    // ---- the headline ----

    [Fact]
    public async Task Three_confirmed_root_games_produce_standings_that_match_the_hand_computed_ratings()
    {
        var n = await ArrangeAsync();
        await NameAsync(n);

        // Game 1: A beats B (1000 v 1000).
        await PlayAsync(n, aWins: true);
        var after1 = await StandingsAsync(n);
        Assert.Equal([("Alex P", 1016), ("Bea W", 984)], after1.Rows.Select(r => (r.DisplayName, r.Rating)));

        // Game 2: A beats B again (1016 v 984): independent hand calculation.
        await PlayAsync(n, aWins: true);
        var a2 = Expected(1016, 984, aWins: true);
        var b2 = 2000 - a2;
        Assert.Equal(1030.5285, a2, 3);
        var after2 = await StandingsAsync(n);
        Assert.Equal(a2, after2.Rows[0].RatingExact, 6);

        // Game 3: B beats A (a2 v b2).
        await PlayAsync(n, aWins: false);
        var b3 = Expected(b2, a2, aWins: true);
        var a3 = 2000 - b3;
        var final = await StandingsAsync(n);

        Assert.Equal("All time", final.Scope);
        Assert.Equal(3, final.GamesCounted);
        Assert.Equal(["Alex P", "Bea W"], final.Rows.Select(r => r.DisplayName));
        Assert.Equal((a3, b3), (final.Rows[0].RatingExact, final.Rows[1].RatingExact), new PairComparer());
        Assert.Equal((1012, 988), (final.Rows[0].Rating, final.Rows[1].Rating));
        Assert.Equal([1, 2], final.Rows.Select(r => r.Rank));
        Assert.All(final.Rows, r => Assert.Equal(3, r.GamesPlayed));
        Assert.Equal(2000, final.Rows.Sum(r => r.RatingExact), 6); // nothing created or lost
    }

    [Fact]
    public async Task Players_join_by_playing_and_a_pending_result_does_not_count()
    {
        var n = await ArrangeAsync();
        Assert.Empty((await StandingsAsync(n)).Rows);

        var table = await OpenTableAsync(n);
        await SubmitAsync(n, table, aWins: true, confirm: false);

        Assert.Empty((await StandingsAsync(n)).Rows); // waiting for B to confirm: not yet

        var result = (await n.AClient.GetFromJsonAsync<List<ResultDto>>($"{Base(n)}/tables/{table.Id}/results"))![0];
        (await n.BClient.PostAsJsonAsync($"{Base(n)}/results/{result.Id}/confirm", new AnswerResultRequest())).EnsureSuccessStatusCode();
        Assert.Equal(2, (await StandingsAsync(n)).Rows.Count);
    }

    [Fact]
    public async Task Voiding_a_confirmed_result_rebuilds_the_ratings_without_it_in_order()
    {
        var n = await ArrangeAsync();
        var g1 = await PlayAsync(n, aWins: true);
        var g2 = await PlayAsync(n, aWins: true);
        await PlayAsync(n, aWins: false);

        (await n.Admin.PostAsJsonAsync($"{Base(n)}/results/{g2.Id}/resolve", new ResolveResultRequest(ResolveAction.Void))).EnsureSuccessStatusCode();

        // Games 1 and 3 remain: A beats B, then B beats A.
        var a1 = Expected(1000, 1000, true);
        var b1 = 2000 - a1;
        var b3 = Expected(b1, a1, aWins: true);
        var final = await StandingsAsync(n);
        Assert.Equal(2, final.GamesCounted);
        Assert.Equal(b3, final.Rows.Single(r => r.UserId == n.B).RatingExact, 6);
        Assert.Equal(2000 - b3, final.Rows.Single(r => r.UserId == n.A).RatingExact, 6);
        Assert.Equal(2, final.Rows.First().GamesPlayed);
        Assert.NotNull(g1);

        // Voiding the rest empties the table: the ratings leave with their results.
        foreach (var result in (await AllResultsAsync(n)).Where(r => r.Status == ResultStatus.Confirmed))
        {
            await n.Admin.PostAsJsonAsync($"{Base(n)}/results/{result.Id}/resolve", new ResolveResultRequest(ResolveAction.Void));
        }

        Assert.Empty((await StandingsAsync(n)).Rows);
    }

    [Fact]
    public async Task A_corrected_report_replaces_a_voided_one()
    {
        var n = await ArrangeAsync();
        var wrong = await PlayAsync(n, aWins: true);

        (await n.Admin.PostAsJsonAsync($"{Base(n)}/results/{wrong.Id}/resolve", new ResolveResultRequest(ResolveAction.Void))).EnsureSuccessStatusCode();
        await SubmitAsync(n, new TableDto(wrong.TableId, wrong.EventId, wrong.GameId, "Root", 1, 4, null, TableStatus.Open, null, GameNightsVisibility.Public, []), aWins: false, confirm: true);

        var rows = (await StandingsAsync(n)).Rows;
        Assert.Equal((984, 1016), (rows.Single(r => r.UserId == n.A).Rating, rows.Single(r => r.UserId == n.B).Rating));
    }

    // ---- what counts ----

    [Fact]
    public async Task A_table_or_night_that_is_not_public_never_reaches_the_league()
    {
        var n = await ArrangeAsync();
        var privateTable = await OpenTableAsync(n);
        var openTable = await OpenTableAsync(n);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            (await db.GameNightTables.FirstAsync(t => t.Id == privateTable.Id)).Visibility = GameNightsVisibility.InviteOnly;
            await db.SaveChangesAsync();
        }

        await SubmitAsync(n, privateTable, aWins: true, confirm: true, viaBot: true);
        Assert.Empty((await StandingsAsync(n)).Rows);

        await SubmitAsync(n, openTable, aWins: true, confirm: true);
        Assert.Equal(2, (await StandingsAsync(n)).Rows.Count);
        Assert.Equal(1, (await StandingsAsync(n)).GamesCounted);

        // The counted table is flagged as feeding a league; the private one is not.
        await using var scope2 = fixture.Factory.Services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        Assert.True(await db2.GameNightTables.AnyAsync(t => t.Id == openTable.Id && t.InLeague));
    }

    [Fact]
    public async Task The_database_refuses_to_make_a_counted_table_or_night_private()
    {
        var n = await ArrangeAsync();
        var result = await PlayAsync(n, aWins: true);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();

        // The table feeds the league, so it is flagged; trying to hide it fails in the database itself.
        var table = await db.GameNightTables.FirstAsync(t => t.Id == result.TableId);
        Assert.True(table.InLeague);
        table.Visibility = GameNightsVisibility.Restricted;
        var tableError = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("CK_GameNightTables_LeagueNeedsPublic", tableError.InnerException!.Message);
        db.ChangeTracker.Clear();

        var night = await db.GameNightEventExtensions.FirstAsync(x => x.EventId == n.EventId);
        Assert.True(night.InLeague);
        night.Visibility = GameNightsVisibility.InviteOnly;
        var nightError = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("CK_GameNightEventExtensions_LeagueNeedsPublic", nightError.InnerException!.Message);
    }

    [Fact]
    public async Task A_table_that_never_fed_a_league_can_be_made_private_and_one_that_stops_feeding_can_too()
    {
        var n = await ArrangeAsync();
        var result = await PlayAsync(n, aWins: true);
        (await n.Admin.PostAsJsonAsync($"{Base(n)}/results/{result.Id}/resolve", new ResolveResultRequest(ResolveAction.Void))).EnsureSuccessStatusCode();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var table = await db.GameNightTables.FirstAsync(t => t.Id == result.TableId);
        Assert.False(table.InLeague); // the void took it out of the league
        table.Visibility = GameNightsVisibility.InviteOnly;
        await db.SaveChangesAsync(); // allowed now
        Assert.False((await db.GameNightEventExtensions.FirstAsync(x => x.EventId == n.EventId)).InLeague);
    }

    [Fact]
    public async Task A_game_with_ratings_switched_off_has_no_league_entries()
    {
        var n = await ArrangeAsync();
        (await n.Admin.PutAsJsonAsync($"{Base(n)}/games/{n.Root.Id}", n.Root.Spec with { RatingEnabled = false })).EnsureSuccessStatusCode();

        await PlayAsync(n, aWins: true);

        Assert.Empty((await StandingsAsync(n)).Rows);
    }

    [Fact]
    public async Task Guests_get_ratings_too_and_show_by_their_typed_name()
    {
        var n = await ArrangeAsync();
        var guest = (await (await n.Admin.PostAsJsonAsync($"{Base(n)}/guests", new CreateGuestRequest("Sam T"))).Content.ReadFromJsonAsync<GuestDto>())!;
        var table = await OpenTableAsync(n);
        var report = new ResultSubmission(
        [
            new(n.A, null, 1, new Dictionary<string, string> { ["faction"] = "Marquise de Cat", ["score"] = "30" }),
            new(null, guest.Id, 2, new Dictionary<string, string> { ["faction"] = "Vagabond", ["score"] = "10" }),
        ]);

        var created = await n.AClient.PostAsJsonAsync($"{Base(n)}/tables/{table.Id}/results", new SubmitResultRequest(report));
        Assert.Equal(ResultStatus.Confirmed, (await created.Content.ReadFromJsonAsync<ResultDto>())!.Status); // one member, so it settles at once

        var rows = (await StandingsAsync(n)).Rows;
        Assert.Equal(["Sam T"], rows.Where(r => r.GuestId is not null).Select(r => r.DisplayName));
        Assert.Equal((1016, 984), (rows.Single(r => r.UserId == n.A).Rating, rows.Single(r => r.GuestId == guest.Id).Rating));
    }

    // ---- history and "my rating" ----

    [Fact]
    public async Task A_players_history_lists_each_game_with_before_and_after_and_my_rating_gives_their_place()
    {
        var n = await ArrangeAsync();
        await PlayAsync(n, aWins: true);
        await PlayAsync(n, aWins: false);

        var history = await n.AClient.GetFromJsonAsync<List<RatingHistoryEntryDto>>($"{Base(n)}/games/{n.Root.Id}/league/history");
        Assert.Equal(2, history!.Count);
        Assert.Equal((1000d, 1016d), (history[0].Before, history[0].After));
        Assert.Equal(1016d, history[1].Before);
        Assert.Equal(1, history[0].Placement);

        var mine = await n.BClient.GetFromJsonAsync<List<MyRatingDto>>($"{Base(n)}/leagues/me");
        var root = Assert.Single(mine!);
        Assert.Equal(("Root", "All time", 2, 2), (root.GameName, root.Scope, root.Players, root.GamesPlayed));
        Assert.Equal(1, root.Rank); // B won the second game, so B (about 1001) is ahead of A (about 999)
        var bot = await Bot.GetFromJsonAsync<List<MyRatingDto>>($"{Base(n)}/leagues/me?userId={n.A}");
        Assert.Single(bot!);
        Assert.Equal(HttpStatusCode.BadRequest, (await Bot.GetAsync($"{Base(n)}/leagues/me")).StatusCode);
    }

    // ---- seasons ----

    [Fact]
    public async Task A_new_season_starts_everyone_fresh_while_the_all_time_rating_carries_on()
    {
        var n = await ArrangeAsync();
        var now = DateTimeOffset.UtcNow;
        // Season 1 covers tonight (in 3 hours); season 2 starts in two days, ending season 1 as it begins.
        var one = await n.Admin.PostAsJsonAsync($"{Base(n)}/games/{n.Root.Id}/league/seasons", new CreateSeasonRequest("Autumn", now.AddHours(-1)));
        Assert.Equal(HttpStatusCode.Created, one.StatusCode);
        var two = await n.Admin.PostAsJsonAsync($"{Base(n)}/games/{n.Root.Id}/league/seasons", new CreateSeasonRequest("Winter", now.AddDays(2)));
        var winter = (await two.Content.ReadFromJsonAsync<SeasonDto>())!;
        var seasons = (await StandingsAsync(n)).Seasons;
        Assert.Equal(["Winter", "Autumn"], seasons.Select(s => s.Name));
        Assert.NotNull(seasons[1].EndsAtUtc); // Autumn was ended when Winter began

        await PlayAsync(n, aWins: true, night: "in 3 hours");   // Autumn
        await PlayAsync(n, aWins: true, night: "in 3 days");    // Winter

        var autumn = await StandingsAsync(n, $"?seasonId={seasons[1].Id}");
        var winterTable = await StandingsAsync(n, $"?seasonId={winter.Id}");
        var allTime = await StandingsAsync(n, "?allTime=true");
        Assert.Equal((1016, "Autumn"), (autumn.Rows[0].Rating, autumn.Scope));
        Assert.Equal((1016, "Winter"), (winterTable.Rows[0].Rating, winterTable.Scope)); // fresh start, not 1030
        Assert.Equal(Expected(1016, 984, true), allTime.Rows[0].RatingExact, 6);
        Assert.Equal(2, allTime.GamesCounted);
        Assert.All(new[] { autumn, winterTable }, t => Assert.Equal(1, t.GamesCounted));

        // With no season named, the table shown is the season running right now (Autumn began an hour ago and runs until Winter).
        Assert.Equal("Autumn", (await StandingsAsync(n)).Scope);
    }

    [Fact]
    public async Task Ending_a_season_and_overlaps_are_handled_and_only_organisers_run_seasons()
    {
        var n = await ArrangeAsync();
        var now = DateTimeOffset.UtcNow;
        var seasons = $"{Base(n)}/games/{n.Root.Id}/league/seasons";

        Assert.Equal(HttpStatusCode.Forbidden, (await n.AClient.PostAsJsonAsync(seasons, new CreateSeasonRequest("Nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await n.Admin.PostAsJsonAsync(seasons, new CreateSeasonRequest("  "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await n.Admin.PostAsJsonAsync(seasons, new CreateSeasonRequest("Bad", now, now.AddDays(-1)))).StatusCode);

        var first = (await (await n.Admin.PostAsJsonAsync(seasons, new CreateSeasonRequest("S1", now.AddDays(-5)))).Content.ReadFromJsonAsync<SeasonDto>())!;
        // A season that would start before the running one began overlaps it.
        Assert.Equal(HttpStatusCode.Conflict, (await n.Admin.PostAsJsonAsync(seasons, new CreateSeasonRequest("S0", now.AddDays(-10), now.AddDays(-2)))).StatusCode);

        var ended = await n.Admin.PostAsync($"{seasons}/{first.Id}/end", null);
        Assert.NotNull((await ended.Content.ReadFromJsonAsync<SeasonDto>())!.EndsAtUtc);
        Assert.Equal(HttpStatusCode.Conflict, (await n.Admin.PostAsync($"{seasons}/{first.Id}/end", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await n.Admin.PostAsync($"{seasons}/{Guid.NewGuid()}/end", null)).StatusCode);
    }

    [Fact]
    public async Task Only_moderators_can_force_a_rebuild_and_it_changes_nothing_when_nothing_is_wrong()
    {
        var n = await ArrangeAsync();
        await PlayAsync(n, aWins: true);
        var before = await StandingsAsync(n);

        Assert.Equal(HttpStatusCode.Forbidden, (await n.AClient.PostAsync($"{Base(n)}/games/{n.Root.Id}/league/recompute", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await n.Admin.PostAsync($"{Base(n)}/games/{n.Root.Id}/league/recompute", null)).StatusCode);

        var after = await StandingsAsync(n);
        Assert.Equal(before.Rows.Select(r => r.RatingExact), after.Rows.Select(r => r.RatingExact));
    }

    // ---- retention ----

    [Fact]
    public async Task The_retention_purge_leaves_results_ratings_rating_events_and_leagues_alone()
    {
        var n = await ArrangeAsync();
        await PlayAsync(n, aWins: true);
        await PlayAsync(n, aWins: false);

        async Task<int[]> CountsAsync()
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            return
            [
                await db.GameNightResults.CountAsync(), await db.GameNightResultPlayers.CountAsync(),
                await db.GameNightLeagues.CountAsync(), await db.GameNightPlayerRatings.CountAsync(), await db.GameNightRatingEvents.CountAsync(),
                await db.GameNightGuests.CountAsync(),
            ];
        }

        var before = await CountsAsync();
        Assert.All(before.Take(5), c => Assert.True(c > 0));

        // Far past any window: everything the purge may delete is old enough. League data must not be on that list.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RetentionService>().PurgeAsync(Instant.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(800)), default);
        }

        Assert.Equal(before, await CountsAsync());
        Assert.Equal(2, (await StandingsAsync(n)).Rows.Count);
    }

    // ---- helpers ----

    private async Task<Night> ArrangeAsync()
    {
        var guild = NewGuildId();
        (await Bot.PutAsJsonAsync($"/guilds/{guild}/presence", new GuildPresenceRequest(true, "League test guild"))).EnsureSuccessStatusCode();
        var (admin, _) = await fixture.LoginAsync(guild * 1000 + 1, (guild, "G", true));
        var (a, sessionA) = await fixture.LoginAsync(guild * 1000 + 2, (guild, "G", false));
        var (b, sessionB) = await fixture.LoginAsync(guild * 1000 + 3, (guild, "G", false));
        (await admin.PostAsync($"/guilds/{guild}/game-nights/games/seed", null)).EnsureSuccessStatusCode();
        var root = (await admin.GetFromJsonAsync<List<GameDto>>($"/guilds/{guild}/game-nights/games"))!.Single(g => g.Spec.Key == "root");
        var ev = await Bot.PostAsJsonAsync($"/guilds/{guild}/events", new CreateEventRequest(9000, "Game night", "in 3 hours", 70000 + guild % 1000));
        var eventId = (await ev.Content.ReadFromJsonAsync<EventDto>())!.Id;
        return new Night(guild, admin, sessionA.UserId, a, sessionB.UserId, b, root, eventId);
    }

    private async Task NameAsync(Night n) =>
        (await Bot.PutAsJsonAsync($"{Base(n)}/member-names", new PutMemberNamesRequest([new(n.A, "Alex P"), new(n.B, "Bea W")]))).EnsureSuccessStatusCode();

    private async Task<TableDto> OpenTableAsync(Night n, Guid? eventId = null) =>
        (await (await n.Admin.PostAsJsonAsync($"{Base(n)}/events/{eventId ?? n.EventId}/tables", new CreateTableRequest(n.Root.Id)))
            .Content.ReadFromJsonAsync<TableDto>())!;

    /// <summary>Reports a two-player Root game (A and B) on a table; the winner is whoever the flag says.</summary>
    private async Task<ResultDto> SubmitAsync(Night n, TableDto table, bool aWins, bool confirm, bool viaBot = false)
    {
        var report = new ResultSubmission(
        [
            new(n.A, null, aWins ? 1 : 2, new Dictionary<string, string> { ["faction"] = "Marquise de Cat", ["score"] = aWins ? "30" : "10" }),
            new(n.B, null, aWins ? 2 : 1, new Dictionary<string, string> { ["faction"] = "Vagabond", ["score"] = aWins ? "10" : "30" }),
        ]);
        var response = viaBot
            ? await Bot.PostAsJsonAsync($"{Base(n)}/tables/{table.Id}/results", new SubmitResultRequest(report, ReporterUserId: n.A))
            : await n.AClient.PostAsJsonAsync($"{Base(n)}/tables/{table.Id}/results", new SubmitResultRequest(report));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var result = (await response.Content.ReadFromJsonAsync<ResultDto>())!;
        if (confirm && result.Status == ResultStatus.Pending)
        {
            var answered = viaBot
                ? await Bot.PostAsJsonAsync($"{Base(n)}/results/{result.Id}/confirm", new AnswerResultRequest(n.B))
                : await n.BClient.PostAsJsonAsync($"{Base(n)}/results/{result.Id}/confirm", new AnswerResultRequest());
            answered.EnsureSuccessStatusCode();
            result = (await answered.Content.ReadFromJsonAsync<ResultDto>())!;
        }

        return result;
    }

    /// <summary>Opens a new table on a night and plays one confirmed game on it.</summary>
    private async Task<ResultDto> PlayAsync(Night n, bool aWins, string night = "in 3 hours")
    {
        var eventId = night == "in 3 hours" ? n.EventId : (await (await Bot.PostAsJsonAsync($"/guilds/{n.Guild}/events",
            new CreateEventRequest(9000, $"Night {night}", night, 71000 + n.Guild % 1000))).Content.ReadFromJsonAsync<EventDto>())!.Id;
        return await SubmitAsync(n, await OpenTableAsync(n, eventId), aWins, confirm: true);
    }

    private async Task<LeagueStandingsDto> StandingsAsync(Night n, string query = "") =>
        (await n.AClient.GetFromJsonAsync<LeagueStandingsDto>($"{Base(n)}/games/{n.Root.Id}/league{query}"))!;

    private async Task<List<ResultDto>> AllResultsAsync(Night n)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var tableIds = await db.GameNightTables.Where(t => t.EventId == n.EventId || db.Events.Any(e => e.Id == t.EventId && e.GuildId == n.Guild)).Select(t => t.Id).ToListAsync();
        var all = new List<ResultDto>();
        foreach (var id in tableIds)
        {
            all.AddRange((await n.Admin.GetFromJsonAsync<List<ResultDto>>($"{Base(n)}/tables/{id}/results"))!);
        }

        return all;
    }

    private sealed class PairComparer : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) x, (double, double) y) => Math.Abs(x.Item1 - y.Item1) < 1e-6 && Math.Abs(x.Item2 - y.Item2) < 1e-6;

        public int GetHashCode((double, double) obj) => 0;
    }
}
