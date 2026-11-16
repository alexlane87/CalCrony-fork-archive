using CalCrony.Api.GameNights;
using CalCrony.Contracts.GameNights;
using Microsoft.Extensions.Configuration;

namespace CalCrony.Api.Tests.GameNights;

/// <summary>The shipped games file and the pure rules: definitions (<see cref="GameRules"/>) and result
/// reports (<see cref="ResultValidator"/>). No database.</summary>
public class GameRulesTests
{
    private static readonly SeedFile Shipped = GameSeedFile.Load(new ConfigurationBuilder().Build());

    private static GameSpec Game(string key) => Shipped.Games.Single(g => g.Key == key).ToSpec();

    private static GameFieldSpec Field(GameSpec game, string key) => game.Fields.Single(f => f.Key == key);

    private static SubmittedPlayer Player(long id, int placement, string? faction = null, string? score = "10", string? colour = null) =>
        new(id, null, placement, new Dictionary<string, string>
        {
            ["faction"] = faction!, ["score"] = score!, ["colour"] = colour!,
        }.Where(kv => kv.Value is not null).ToDictionary());

    // ---- the shipped file ----

    [Fact]
    public void The_shipped_file_defines_root_and_arcs_with_the_agreed_questions()
    {
        Assert.Equal(["arcs", "root"], Shipped.Games.Select(g => g.Key).Order());

        var root = Game("root");
        Assert.Equal(GameResultType.RankedPlacement, root.ResultType);
        Assert.Equal((2, 6, 4), (root.MinSize, root.MaxSize, root.DefaultSize));
        Assert.Equal([("players", true), ("faction", true), ("score", true), ("left_out", false), ("seat", false), ("map", false), ("landmarks", false), ("deck", false)],
            root.Fields.Select(f => (f.Key, f.Required)));
        Assert.True(Field(root, "faction").Unique);

        var arcs = Game("arcs");
        Assert.Equal((2, 4, 4), (arcs.MinSize, arcs.MaxSize, arcs.DefaultSize));
        Assert.Equal([("players", true), ("colour", true), ("score", true), ("leader", false), ("round_ended", false)],
            arcs.Fields.Select(f => (f.Key, f.Required)));
        Assert.False(Field(arcs, "round_ended").Required); // not certain the base game records it
    }

    [Fact]
    public void The_shipped_games_satisfy_the_definition_rules()
    {
        foreach (var game in Shipped.Games)
        {
            Assert.Empty(GameRules.Validate(game.ToSpec()));
        }
    }

    [Fact]
    public void A_broken_games_file_names_every_problem()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GameSeedFile.Parse("""
            { "games": [
              { "version": 1, "key": "Bad Key", "name": "", "resultType": "RankedPlacement",
                "minSize": 1, "maxSize": 200, "defaultSize": 4, "ratingEnabled": true, "ratingK": 32, "startRating": 1000,
                "fields": [ { "key": "pick", "label": "Pick", "kind": "PickList", "scope": "Player", "required": true } ] }
            ] }
            """));

        Assert.Contains("Bad Key", ex.Message);
        Assert.Contains("table sizes", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pick-list", ex.Message);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed_in_the_file()
    {
        var file = GameSeedFile.Parse("""
            { // the games
              "games": [
                { "version": 1, "key": "chess", "name": "Chess", "resultType": "SingleWinner",
                  "minSize": 2, "maxSize": 2, "defaultSize": 2, "ratingEnabled": false, "ratingK": 32, "startRating": 1000,
                  "fields": [ { "key": "players", "label": "Players", "kind": "Player", "scope": "Table", "required": true }, ], },
              ],
            }
            """);

        Assert.Equal("chess", Assert.Single(file.Games).Key);
    }

    [Theory]
    [InlineData("1", 1, 6, 4, false)]
    [InlineData("2", 2, 101, 4, false)]
    [InlineData("3", 5, 4, 4, false)]
    [InlineData("4", 2, 6, 7, false)]
    [InlineData("5", 2, 100, 100, true)]
    public void Table_sizes_run_from_2_to_100_and_the_default_sits_inside(string tag, int min, int max, int @default, bool ok)
    {
        var spec = new GameSpec($"g{tag}", "G", GameResultType.RankedPlacement, min, max, @default, true, 32, 1000, []);

        var sizeProblems = GameRules.Validate(spec).Where(p => p.Contains("size", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(ok, !sizeProblems.Any());
    }

    [Fact]
    public void Question_rules_catch_duplicate_keys_short_pick_lists_and_misplaced_flags()
    {
        var spec = new GameSpec("g", "G", GameResultType.RankedPlacement, 2, 4, 4, true, 32, 1000,
        [
            new("a", "A", GameFieldKind.PickList, GameFieldScope.Player, true, Values: ["only one"]),
            new("a", "A again", GameFieldKind.Number, GameFieldScope.Table, false, Unique: true),
            new("b", "B", GameFieldKind.Text, GameFieldScope.Table, false, Values: ["nope"]),
            new("players", "Players", GameFieldKind.Player, GameFieldScope.Player, false),
        ]);

        var problems = string.Join("\n", GameRules.Validate(spec));

        Assert.Contains("used twice", problems);
        Assert.Contains("2 to 50 distinct values", problems);
        Assert.Contains("'unique' only applies", problems);
        Assert.Contains("only a pick-list has values", problems);
        Assert.Contains("always required and table-wide", problems);
    }

    // ---- result validation ----

    [Fact]
    public void A_complete_root_report_is_accepted()
    {
        var root = Game("root");
        var report = new ResultSubmission(
        [
            Player(1, 1, "Marquise de Cat", "30"), Player(2, 2, "Eyrie Dynasties", "25"),
            Player(3, 3, "Woodland Alliance", "20"), Player(4, 4, "Vagabond", "12"),
        ],
            new Dictionary<string, string> { ["map"] = "Autumn", ["deck"] = "Standard" });

        Assert.Empty(ResultValidator.Validate(root, report));
    }

    [Fact]
    public void A_complete_arcs_report_is_accepted_with_the_optional_questions_left_out()
    {
        var arcs = Game("arcs");
        var report = new ResultSubmission(
        [
            Player(1, 1, score: "28", colour: "Red"), Player(2, 2, score: "21", colour: "Blue"), Player(3, 3, score: "17", colour: "White"),
        ]);

        Assert.Empty(ResultValidator.Validate(arcs, report));
    }

    [Fact]
    public void Missing_required_answers_are_reported_by_question_and_player()
    {
        var root = Game("root");
        var report = new ResultSubmission(
        [
            new(1, null, 1, new Dictionary<string, string> { ["score"] = "30" }),
            new(2, null, 2, new Dictionary<string, string> { ["faction"] = "Vagabond" }),
        ]);

        var problems = ResultValidator.Validate(root, report);

        Assert.Contains(problems, p => p.Field == "faction" && p.Message.Contains("player 1"));
        Assert.Contains(problems, p => p.Field == "score" && p.Message.Contains("player 2"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void A_table_outside_the_games_size_range_is_rejected(int players)
    {
        var root = Game("root");
        var report = new ResultSubmission(
            [.. Enumerable.Range(1, players).Select(i => Player(i, i, root.Fields.Single(f => f.Key == "faction").Values![i % 10], "5"))]);

        Assert.Contains(ResultValidator.Validate(root, report), p => p.Message.Contains("needs 2 to 6 players"));
    }

    [Fact]
    public void The_same_player_twice_and_a_player_with_no_identity_are_rejected()
    {
        var arcs = Game("arcs");
        var twice = new ResultSubmission([Player(1, 1, score: "5", colour: "Red"), Player(1, 2, score: "4", colour: "Blue")]);
        var neither = new ResultSubmission([new(null, null, 1), Player(2, 2, score: "4", colour: "Blue")]);
        var both = new ResultSubmission([new(1, Guid.NewGuid(), 1), Player(2, 2, score: "4", colour: "Blue")]);

        Assert.Contains(ResultValidator.Validate(arcs, twice), p => p.Message == "A player is listed twice.");
        Assert.Contains(ResultValidator.Validate(arcs, neither), p => p.Message.Contains("either a member or a guest"));
        Assert.Contains(ResultValidator.Validate(arcs, both), p => p.Message.Contains("either a member or a guest"));
    }

    [Fact]
    public void A_guest_counts_as_a_player()
    {
        var arcs = Game("arcs");
        var report = new ResultSubmission(
        [
            Player(1, 1, score: "9", colour: "Red"),
            new(null, Guid.NewGuid(), 2, new Dictionary<string, string> { ["score"] = "7", ["colour"] = "Blue" }),
        ]);

        Assert.Empty(ResultValidator.Validate(arcs, report));
    }

    [Fact]
    public void Two_players_cannot_share_a_unique_answer()
    {
        var arcs = Game("arcs");
        var report = new ResultSubmission([Player(1, 1, score: "9", colour: "Red"), Player(2, 2, score: "7", colour: "red")]);

        Assert.Contains(ResultValidator.Validate(arcs, report), p => p.Field == "colour" && p.Message.Contains("No two players"));
    }

    [Fact]
    public void Answers_are_checked_against_their_kind()
    {
        var root = Game("root");
        var report = new ResultSubmission(
            [Player(1, 1, "Marquise de Cat", "thirty"), Player(2, 2, "Not a faction", "10")],
            new Dictionary<string, string> { ["map"] = "Atlantis", ["round"] = "3" });

        var problems = ResultValidator.Validate(root, report);

        Assert.Contains(problems, p => p.Field == "score" && p.Message.Contains("whole number"));
        Assert.Contains(problems, p => p.Field == "faction" && p.Message.Contains("not one of the choices"));
        Assert.Contains(problems, p => p.Field == "map" && p.Message.Contains("not one of the choices"));
        Assert.Contains(problems, p => p.Field == "round" && p.Message.Contains("not a question"));
    }

    [Fact]
    public void Ranked_placements_need_a_first_place_within_range_and_ties_are_fine()
    {
        var arcs = Game("arcs");
        SubmittedPlayer P(long id, int? place, string colour) =>
            new(id, null, place, new Dictionary<string, string> { ["score"] = "1", ["colour"] = colour });

        Assert.Empty(ResultValidator.Validate(arcs, new ResultSubmission([P(1, 1, "Red"), P(2, 1, "Blue"), P(3, 3, "White")])));
        Assert.Contains(ResultValidator.Validate(arcs, new ResultSubmission([P(1, null, "Red"), P(2, 2, "Blue")])),
            p => p.Message.Contains("finishing place"));
        Assert.Contains(ResultValidator.Validate(arcs, new ResultSubmission([P(1, 1, "Red"), P(2, 5, "Blue")])),
            p => p.Message.Contains("between 1 and 2"));
        Assert.Contains(ResultValidator.Validate(arcs, new ResultSubmission([P(1, 2, "Red"), P(2, 2, "Blue")])),
            p => p.Message.Contains("finished first"));
    }

    [Fact]
    public void Single_winner_and_winning_side_games_have_their_own_placement_rules()
    {
        var winner = new GameSpec("w", "W", GameResultType.SingleWinner, 2, 4, 2, false, 32, 1000, []);
        var side = new GameSpec("s", "S", GameResultType.WinningSide, 2, 8, 4, false, 32, 1000, []);
        SubmittedPlayer P(long id, int? place) => new(id, null, place);

        Assert.Empty(ResultValidator.Validate(winner, new ResultSubmission([P(1, 1), P(2, null), P(3, null)])));
        Assert.NotEmpty(ResultValidator.Validate(winner, new ResultSubmission([P(1, 1), P(2, 1)])));
        Assert.NotEmpty(ResultValidator.Validate(winner, new ResultSubmission([P(1, null), P(2, null)])));

        Assert.Empty(ResultValidator.Validate(side, new ResultSubmission([P(1, 1), P(2, 1), P(3, 2), P(4, 2)])));
        Assert.NotEmpty(ResultValidator.Validate(side, new ResultSubmission([P(1, 1), P(2, 1)])));
        Assert.NotEmpty(ResultValidator.Validate(side, new ResultSubmission([P(1, 1), P(2, 3)])));
    }

    [Theory]
    [InlineData("1, 2,3", new long[] { 1, 2, 3 })]
    [InlineData("5;5 , x, -4, 0, 7", new long[] { 5, 7 })]
    [InlineData("", new long[0])]
    [InlineData(null, new long[0])]
    public void Seed_guild_ids_are_parsed_forgivingly(string? text, long[] expected) =>
        Assert.Equal(expected, GameNightsServiceCollectionExtensions.ParseGuildIds(text));
}
