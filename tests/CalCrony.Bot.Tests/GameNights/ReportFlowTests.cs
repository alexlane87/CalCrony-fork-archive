using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalCrony.Bot.GameNights;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Discord;

namespace CalCrony.Bot.Tests.GameNights;

/// <summary>The real Root and Arcs, exactly as the API seeds them.</summary>
internal static class SeededGames
{
    private sealed record Entry(
        int Version, string Key, string Name, GameResultType ResultType, int MinSize, int MaxSize, int DefaultSize,
        bool RatingEnabled, int RatingK, int StartRating, IReadOnlyList<GameFieldSpec> Fields);

    private sealed record Seed(IReadOnlyList<Entry> Games);

    private static readonly Lazy<IReadOnlyList<GameSpec>> All = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("games.json")!;
        var seed = JsonSerializer.Deserialize<Seed>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } })!;
        return [.. seed.Games.Select(g => new GameSpec(g.Key, g.Name, g.ResultType, g.MinSize, g.MaxSize, g.DefaultSize, g.RatingEnabled, g.RatingK, g.StartRating, g.Fields))];
    });

    public static GameSpec Root => All.Value.Single(g => g.Key == "root");

    public static GameSpec Arcs => All.Value.Single(g => g.Key == "arcs");
}

/// <summary>Plays the whole report flow, with no Discord at all, for the games that ship: every move a reporter makes,
/// what the next screen offers, and what finally goes to the API. A fake "Discord" is simply the flow's own operations
/// called in the order the interactions would call them.</summary>
public class ReportFlowTests
{
    private static readonly long[] Four = [11, 12, 13, 14];

    private static Dictionary<long, string> Names(params long[] ids) => ids.ToDictionary(i => i, i => $"Player {i}");

    private static ReportSession StartRoot(GameSpec? game = null, params long[] seated) =>
        ReportFlow.Start(1, 2, Guid.NewGuid(), game ?? SeededGames.Root, seated.Length == 0 ? Four : seated, Names(seated.Length == 0 ? Four : seated));

    [Fact]
    public void A_root_report_goes_through_every_step_and_produces_a_submission_the_validator_accepts()
    {
        var s = StartRoot();

        // The seated players are already chosen, so the first Next just works.
        Assert.Equal(ReportStepKind.Players, s.Current.Kind);
        Assert.Null(ReportFlow.Next(s));

        // Places: nothing yet is refused with a reason.
        Assert.Equal(ReportStepKind.Places, s.Current.Kind);
        Assert.Contains("finishing place", ReportFlow.Next(s));
        for (var i = 0; i < 4; i++)
        {
            ReportFlow.SetPlace(s, Four[i], i + 1);
        }

        Assert.Null(ReportFlow.Next(s));

        // Required faction for everyone.
        Assert.Equal(ReportStepKind.PlayerPick, s.Current.Kind);
        Assert.Equal("faction", s.Current.FieldKey);
        Assert.NotNull(ReportFlow.Next(s));
        var factions = new[] { "Marquise de Cat", "Eyrie Dynasties", "Woodland Alliance", "Vagabond" };
        for (var i = 0; i < 4; i++)
        {
            ReportFlow.SetPlayerPick(s, "faction", Four[i], factions[i]);
        }

        Assert.Null(ReportFlow.Next(s));

        // Required scores, in a form.
        Assert.Equal(ReportStepKind.Inputs, s.Current.Kind);
        Assert.True(s.Current.Required);
        Assert.Contains("Fill in the form", ReportFlow.Next(s));
        Assert.Null(ReportFlow.ApplyInputs(s, s.Current, new Dictionary<int, string> { [0] = "30", [1] = "25", [2] = "20", [3] = "12" }));
        Assert.Null(ReportFlow.Next(s));

        // Optional steps follow, and can be skipped or answered.
        var optional = new List<string>();
        while (s.Current.Kind is ReportStepKind.TablePick or ReportStepKind.Inputs)
        {
            Assert.False(s.Current.Required);
            optional.Add($"{s.Current.Kind}:{s.Current.FieldKey}");
            if (s.Current.FieldKey == "map")
            {
                ReportFlow.SetTablePick(s, "map", "Autumn");
                Assert.Null(ReportFlow.Next(s));
            }
            else
            {
                Assert.Null(ReportFlow.Skip(s));
            }
        }

        Assert.Equal(["TablePick:left_out", "TablePick:map", "TablePick:deck", "Inputs:"], optional);

        // Photo: skippable here, then the summary.
        Assert.Equal(ReportStepKind.Photo, s.Current.Kind);
        Assert.Null(ReportFlow.Skip(s));
        Assert.Equal(ReportStepKind.Summary, s.Current.Kind);
        Assert.Empty(ReportFlow.Problems(s));

        var request = ReportFlow.ToRequest(s);
        Assert.Equal(1L, request.ReporterUserId);
        Assert.Empty(ResultValidator.Validate(SeededGames.Root, request.Submission));
        var winner = request.Submission.Players[0];
        Assert.Equal((11L, 1, "Marquise de Cat", "30"), (winner.UserId, winner.Placement, winner.Values!["faction"], winner.Values["score"]));
        Assert.Equal(new Dictionary<string, string> { ["map"] = "Autumn" }, request.Submission.TableValues);
    }

    [Fact]
    public void An_arcs_report_asks_for_colours_and_scores_and_the_round_it_ended()
    {
        long[] three = [21, 22, 23];
        var s = ReportFlow.Start(1, 2, Guid.NewGuid(), SeededGames.Arcs, three, Names(three));

        Assert.Equal(
            [ReportStepKind.Players, ReportStepKind.Places, ReportStepKind.PlayerPick, ReportStepKind.Inputs, ReportStepKind.Inputs, ReportStepKind.Photo, ReportStepKind.Summary],
            s.Steps.Select(x => x.Kind));
        Assert.Null(ReportFlow.Next(s));
        for (var i = 0; i < 3; i++)
        {
            ReportFlow.SetPlace(s, three[i], i + 1);
        }

        ReportFlow.Next(s);
        var colours = new[] { "Red", "Blue", "White" };
        for (var i = 0; i < 3; i++)
        {
            ReportFlow.SetPlayerPick(s, "colour", three[i], colours[i]);
        }

        ReportFlow.Next(s);
        Assert.Null(ReportFlow.ApplyInputs(s, s.Current, new Dictionary<int, string> { [0] = "28", [1] = "21", [2] = "17" }));
        ReportFlow.Next(s);
        // The optional form holds the leader of each player and the round the game ended (4 items).
        Assert.False(s.Current.Required);
        Assert.Equal(4, s.Current.Items!.Count);
        Assert.Null(ReportFlow.ApplyInputs(s, s.Current, new Dictionary<int, string> { [3] = "5" }));
        ReportFlow.Next(s);
        ReportFlow.SkipPhoto(s);
        ReportFlow.Next(s);

        var request = ReportFlow.ToRequest(s);
        Assert.Empty(ReportFlow.Problems(s));
        Assert.Empty(ResultValidator.Validate(SeededGames.Arcs, request.Submission));
        Assert.Equal("5", request.Submission.TableValues!["round_ended"]);
    }

    [Fact]
    public void Required_steps_come_before_optional_ones_and_the_photo_and_summary_come_last()
    {
        var steps = SeededGames.Root.Fields.Count > 0 ? StartRoot().Steps : [];

        var firstOptional = steps.ToList().FindIndex(x => !x.Required && x.Kind is not (ReportStepKind.Photo));
        Assert.True(firstOptional > 0);
        Assert.All(steps.Take(firstOptional), x => Assert.True(x.Required));
        Assert.Equal([ReportStepKind.Photo, ReportStepKind.Summary], steps.TakeLast(2).Select(x => x.Kind));
    }

    [Fact]
    public void Typed_answers_are_split_into_forms_of_five_which_is_discords_limit()
    {
        long[] six = [31, 32, 33, 34, 35, 36];
        var s = ReportFlow.Start(1, 2, Guid.NewGuid(), SeededGames.Root, six, Names(six));

        var forms = s.Steps.Where(x => x.Kind == ReportStepKind.Inputs).ToList();

        Assert.All(forms, f => Assert.InRange(f.Items!.Count, 1, 5));
        Assert.Equal([5, 1], forms.Where(f => f.Required).Select(f => f.Items!.Count)); // six scores
        Assert.Equal([5, 2], forms.Where(f => !f.Required).Select(f => f.Items!.Count)); // six seats and the landmarks
    }

    [Fact]
    public void A_wrong_number_or_a_missing_required_answer_keeps_nothing_from_that_form()
    {
        var s = StartRoot();
        while (s.Current.Kind != ReportStepKind.Inputs)
        {
            if (s.Current.Kind == ReportStepKind.Places)
            {
                for (var i = 0; i < 4; i++) ReportFlow.SetPlace(s, Four[i], i + 1);
            }

            if (s.Current.Kind == ReportStepKind.PlayerPick)
            {
                foreach (var (id, faction) in Four.Zip(new[] { "Vagabond", "Lizard Cult", "Corvid Conspiracy", "Eyrie Dynasties" })) ReportFlow.SetPlayerPick(s, "faction", id, faction);
            }

            Assert.Null(ReportFlow.Next(s));
        }

        Assert.Contains("whole number", ReportFlow.ApplyInputs(s, s.Current, new Dictionary<int, string> { [0] = "30", [1] = "lots", [2] = "20", [3] = "12" }));
        Assert.All(s.Current.Items!, item => Assert.Null(s.ValueOf(item)));
        Assert.Contains("required", ReportFlow.ApplyInputs(s, s.Current, new Dictionary<int, string> { [0] = "30", [1] = "25", [2] = "20" }));
        Assert.All(s.Current.Items!, item => Assert.Null(s.ValueOf(item)));
    }

    [Fact]
    public void Choosing_different_players_drops_the_answers_of_anyone_no_longer_playing()
    {
        var s = StartRoot();
        ReportFlow.SetPlace(s, 11, 1);
        ReportFlow.SetPlace(s, 14, 4);
        ReportFlow.SetPlayerPick(s, "faction", 14, "Vagabond");

        ReportFlow.SetPlayers(s, [11, 12, 13], Names(11, 12, 13));

        Assert.False(s.Places.ContainsKey(14));
        Assert.False(s.PlayerValues["faction"].ContainsKey(14));
        Assert.True(s.Places.ContainsKey(11));
    }

    [Fact]
    public void Too_few_players_is_refused_with_the_games_own_limits()
    {
        var s = ReportFlow.Start(1, 2, Guid.NewGuid(), SeededGames.Root, [], new Dictionary<long, string>());

        Assert.Contains("needs 2 to 6 players", ReportFlow.Next(s));
        ReportFlow.SetPlayers(s, [1], Names(1));
        Assert.Contains("needs 2 to 6 players", ReportFlow.Next(s));
    }

    // ---- the photo ----

    [Fact]
    public void The_photo_can_be_skipped_when_it_is_not_required()
    {
        var s = StartRoot();
        GoTo(s, ReportStepKind.Photo);

        Assert.True(s.Current.Required == false);
        Assert.Null(ReportFlow.Skip(s));
        Assert.True(s.PhotoSkipped);
        Assert.Empty(ReportFlow.Problems(s));
    }

    [Fact]
    public void When_a_game_requires_the_photo_it_cannot_be_skipped_and_the_report_cannot_be_submitted_without_it()
    {
        var s = StartRoot(SeededGames.Root with { PhotoRequired = true });
        GoTo(s, ReportStepKind.Photo);

        Assert.True(s.Current.Required);
        Assert.Contains("needs the board photo", ReportFlow.Skip(s));
        Assert.Contains("needs the board photo", ReportFlow.Next(s));
        Assert.Contains(ReportFlow.Problems(s), p => p.Contains("photo is required"));

        ReportFlow.AcknowledgePhoto(s);
        Assert.Null(ReportFlow.Next(s));
        Assert.Empty(ReportFlow.Problems(s));
    }

    [Fact]
    public void Nothing_about_a_photo_is_ever_in_what_is_sent()
    {
        var s = StartRoot(SeededGames.Root with { PhotoRequired = true });
        GoTo(s, ReportStepKind.Photo);
        ReportFlow.AcknowledgePhoto(s);

        var json = JsonSerializer.Serialize(ReportFlow.ToRequest(s));

        Assert.DoesNotContain("photo", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", json, StringComparison.OrdinalIgnoreCase);
    }

    // ---- editing, other result types ----

    [Fact]
    public void Edit_goes_back_to_the_places_and_keeps_every_answer_given()
    {
        var s = StartRoot();
        GoTo(s, ReportStepKind.Summary);
        var before = ReportFlow.ToRequest(s).Submission;

        ReportFlow.Edit(s);

        Assert.Equal(ReportStepKind.Places, s.Current.Kind);
        Assert.Equal(before.Players.Select(p => p.Values!["faction"]), ReportFlow.ToRequest(s).Submission.Players.Select(p => p.Values!["faction"]));
    }

    [Fact]
    public void A_single_winner_game_asks_who_won_and_sends_only_the_winners_place()
    {
        var game = new GameSpec("chess", "Chess", GameResultType.SingleWinner, 2, 2, 2, false, 32, 1000,
            [new("players", "Players", GameFieldKind.Player, GameFieldScope.Table, true)]);
        var s = ReportFlow.Start(1, 2, Guid.NewGuid(), game, [41, 42], Names(41, 42));
        ReportFlow.Next(s);

        Assert.Equal("Who won?", s.Current.Title);
        Assert.Contains("Pick who won", ReportFlow.Next(s));
        ReportFlow.SetWinner(s, 42);
        Assert.Null(ReportFlow.Next(s));
        ReportFlow.SkipPhoto(s);

        var submission = ReportFlow.ToRequest(s).Submission;
        Assert.Equal([(41L, (int?)null), (42L, 1)], submission.Players.Select(p => (p.UserId!.Value, p.Placement)));
        Assert.Empty(ResultValidator.Validate(game, submission));
    }

    [Fact]
    public void A_winning_side_game_asks_for_a_side_each_with_both_sides_present()
    {
        var game = new GameSpec("teams", "Teams", GameResultType.WinningSide, 2, 8, 4, false, 32, 1000,
            [new("players", "Players", GameFieldKind.Player, GameFieldScope.Table, true)]);
        var s = ReportFlow.Start(1, 2, Guid.NewGuid(), game, [1, 2, 3, 4], Names(1, 2, 3, 4));
        ReportFlow.Next(s);

        ReportFlow.SetPlace(s, 1, 1);
        ReportFlow.SetPlace(s, 2, 1);
        ReportFlow.SetPlace(s, 3, 1);
        ReportFlow.SetPlace(s, 4, 1);
        Assert.Contains("each", ReportFlow.Next(s)); // all on one side

        ReportFlow.SetPlace(s, 4, 2);
        Assert.Null(ReportFlow.Next(s));
    }

    [Fact]
    public void Games_too_big_for_discord_are_left_to_the_website()
    {
        Assert.True(ReportFlow.IsReportable(SeededGames.Root));
        Assert.True(ReportFlow.IsReportable(SeededGames.Arcs));
        Assert.False(ReportFlow.IsReportable(SeededGames.Root with { MaxSize = 40 }));
        Assert.False(ReportFlow.IsReportable(SeededGames.Root with
        {
            Fields = [.. SeededGames.Root.Fields, new GameFieldSpec("big", "Big", GameFieldKind.PickList, GameFieldScope.Table, false, Values: [.. Enumerable.Range(0, 30).Select(i => $"v{i}")])],
        }));
    }

    // ---- the sessions ----

    [Fact]
    public void Only_the_person_who_started_a_report_can_continue_it_and_it_expires()
    {
        var store = new ReportSessionStore();
        var s = StartRoot();
        store.Add(s);

        Assert.Same(s, store.Get(s.Id, 1));
        Assert.Null(store.Get(s.Id, 2));
        s.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.Null(store.Get(s.Id, 1));
    }

    // ---- what every screen looks like ----

    [Theory]
    [InlineData("root", 2)]
    [InlineData("root", 4)]
    [InlineData("root", 6)]
    [InlineData("arcs", 2)]
    [InlineData("arcs", 4)]
    public void Every_screen_of_every_step_stays_inside_discords_limits(string key, int players)
    {
        var game = key == "root" ? SeededGames.Root : SeededGames.Arcs;
        var ids = Enumerable.Range(1, players).Select(i => (long)(100 + i)).ToArray();
        var s = ReportFlow.Start(1, 2, Guid.NewGuid(), game, ids, Names(ids));
        // Long names must not break labels either.
        foreach (var id in ids) s.Labels[id] = new string('x', 90);

        for (var step = 0; step < s.Steps.Count; step++)
        {
            s.StepIndex = step;
            var pages = s.Current.Kind is ReportStepKind.Places or ReportStepKind.PlayerPick ? (players + 2) / 3 : 1;
            for (var page = 0; page < pages; page++)
            {
                s.Page = page;
                var (text, components) = ReportView.Render(s);
                AssertWithinLimits(text, components);
            }

            if (s.Current.Kind == ReportStepKind.Inputs)
            {
                var form = ReportView.Form(s);
                Assert.InRange(form.Components.Count, 1, 5);
                Assert.Equal(s.Current.Items!.Count, form.Components.Count);
                Assert.InRange(form.CustomId.Length, 1, 100);
            }
        }
    }

    [Fact]
    public void The_summary_lists_players_in_finishing_order_with_their_answers_and_blocks_submit_until_it_is_valid()
    {
        var s = StartRoot();
        GoTo(s, ReportStepKind.Summary);
        var (_, ok) = ReportView.Render(s);
        Assert.False(SubmitButton(ok).IsDisabled);
        var text = ReportView.Summary(s);

        Assert.True(text.IndexOf("Player 11", StringComparison.Ordinal) < text.IndexOf("Player 14", StringComparison.Ordinal));
        Assert.Contains("Faction: Marquise de Cat", text);
        Assert.Contains("Map: Autumn", text);

        // Two players with the same faction: the summary says so and Submit is switched off.
        ReportFlow.SetPlayerPick(s, "faction", 12, "Marquise de Cat");
        var (_, blocked) = ReportView.Render(s);
        Assert.True(SubmitButton(blocked).IsDisabled);
        Assert.Contains("No two players", ReportView.Summary(s));
    }

    // ---- helpers ----

    private static ButtonComponent SubmitButton(MessageComponent c) =>
        c.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>().Single(b => b.CustomId.EndsWith(":submit"));

    /// <summary>Plays a complete, valid Root report up to a step.</summary>
    private static void GoTo(ReportSession s, ReportStepKind kind)
    {
        var factions = new[] { "Marquise de Cat", "Eyrie Dynasties", "Woodland Alliance", "Vagabond" };
        while (s.Current.Kind != kind)
        {
            switch (s.Current.Kind)
            {
                case ReportStepKind.Places:
                    for (var i = 0; i < s.Players.Count; i++) ReportFlow.SetPlace(s, s.Players[i], i + 1);
                    break;
                case ReportStepKind.PlayerPick:
                    for (var i = 0; i < s.Players.Count; i++) ReportFlow.SetPlayerPick(s, "faction", s.Players[i], factions[i % 4]);
                    break;
                case ReportStepKind.TablePick when s.Current.FieldKey == "map":
                    ReportFlow.SetTablePick(s, "map", "Autumn");
                    break;
                case ReportStepKind.Inputs when s.Current.Required:
                    ReportFlow.ApplyInputs(s, s.Current, s.Current.Items!.Select((_, i) => (i, (30 - i).ToString())).ToDictionary(x => x.i, x => x.Item2));
                    break;
                case ReportStepKind.Photo:
                    ReportFlow.SkipPhoto(s);
                    break;
            }

            var problem = s.Current.Required || s.Current.Kind == ReportStepKind.Photo ? ReportFlow.Next(s) : ReportFlow.Skip(s);
            if (s.Current.Kind == ReportStepKind.Photo && problem is not null)
            {
                ReportFlow.AcknowledgePhoto(s);
                ReportFlow.Next(s);
            }
        }
    }

    private static void AssertWithinLimits(string text, MessageComponent components)
    {
        Assert.InRange(text.Length, 1, 2000);
        var rows = components.Components.OfType<ActionRowComponent>().ToList();
        Assert.InRange(rows.Count, 1, 5);
        foreach (var row in rows)
        {
            Assert.InRange(row.Components.Count, 1, 5);
            foreach (var c in row.Components)
            {
                var id = c switch { ButtonComponent b => b.CustomId, SelectMenuComponent m => m.CustomId, _ => "" };
                Assert.InRange(id.Length, 1, 100);
                if (c is SelectMenuComponent menu)
                {
                    Assert.InRange(menu.Options.Count, 0, 25);
                    Assert.All(menu.Options, o => Assert.InRange(o.Label.Length, 1, 100));
                    Assert.All(menu.Options, o => Assert.InRange(o.Value.Length, 1, 100));
                }
            }
        }
    }
}

public class ReportViewAndTextTests
{
    [Fact]
    public void The_report_button_in_a_table_thread_carries_the_table_id()
    {
        var table = Guid.NewGuid();

        var button = TableDeliveryHandler.ReportButton(table).Components.OfType<ActionRowComponent>().Single().Components.OfType<ButtonComponent>().Single();

        Assert.Equal($"gnrepstart:{table}", button.CustomId);
        Assert.Equal("Report game", button.Label);
    }

    private static ResultDto Result(ResultStatus status, params long[] awaiting) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Root", status, 1, DateTimeOffset.UtcNow, null,
        [
            new ResultPlayerDto(11, null, "Alex P", 1, 30, new Dictionary<string, string> { ["faction"] = "Marquise de Cat" }),
            new ResultPlayerDto(12, null, "Bea W", 2, 25, new Dictionary<string, string> { ["faction"] = "Vagabond" }),
        ],
        new Dictionary<string, string> { ["round_ended"] = "5", ["map"] = "Autumn" }, [], awaiting, []);

    [Fact]
    public void A_pending_result_post_shows_the_table_asks_the_others_and_has_confirm_and_reject_buttons()
    {
        var result = Result(ResultStatus.Pending, 12);

        var text = ResultText.Summary(result, 2);
        var buttons = ResultText.Buttons(result.Id).Components.OfType<ActionRowComponent>().Single().Components.OfType<ButtonComponent>().ToList();

        Assert.Contains("**Root, table 2**", text);
        Assert.Contains("1. Alex P: Marquise de Cat, score 30", text);
        Assert.Contains("Round ended: 5", text);
        Assert.Contains("Waiting for <@12>", text);
        Assert.Equal([$"gnres:ok:{result.Id}", $"gnres:no:{result.Id}"], buttons.Select(b => b.CustomId));
        Assert.DoesNotContain("photo", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_confirmed_result_says_so_and_a_settled_notice_exists_only_for_confirmed_and_voided()
    {
        Assert.Contains("✅ Confirmed.", ResultText.Summary(Result(ResultStatus.Confirmed), 1));
        Assert.Contains("confirmed", ResultText.Settled(Result(ResultStatus.Confirmed), 1));
        Assert.Contains("withdrawn", ResultText.Settled(Result(ResultStatus.Voided), 1));
        Assert.Null(ResultText.Settled(Result(ResultStatus.Pending, 12), 1));
        Assert.Null(ResultText.Settled(Result(ResultStatus.Disputed), 1));
    }

    [Fact]
    public void The_reminder_names_only_the_people_who_have_not_answered()
    {
        var text = ResultText.Reminder(Result(ResultStatus.Pending, 12, 13), 3);

        Assert.Contains("<@12>, <@13>", text);
        Assert.DoesNotContain("<@11>", text);
        Assert.Contains("table 3", text);
    }

    [Fact]
    public void Members_with_no_usable_name_are_nudged_to_set_one_and_told_what_to_set()
    {
        Assert.Null(ResultText.Nudge([]));

        var nudge = ResultText.Nudge([12, 14])!;

        Assert.Contains("<@12> <@14>", nudge);
        Assert.Contains("Alex P", nudge);
        Assert.Contains("server nickname", nudge);

        var result = Result(ResultStatus.Pending, 12) with { UnnamedUserIds = [12] };
        Assert.Contains("server nickname", ResultText.Summary(result, 1));
    }

    [Fact]
    public void A_long_result_post_is_kept_under_discords_message_limit()
    {
        var many = Enumerable.Range(1, 60).Select(i => new ResultPlayerDto(i, null, new string('n', 30), i, i, new Dictionary<string, string> { ["k"] = new string('v', 40) })).ToList();

        var text = ResultText.Summary(Result(ResultStatus.Pending, 1) with { Players = many }, 1);

        Assert.True(text.Length <= 1900);
    }

    [Fact]
    public void The_result_handler_owns_the_four_result_notices()
    {
        var handler = new ResultDeliveryHandler(null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<ResultDeliveryHandler>.Instance);

        Assert.All(
            new[] { DeliveryType.ResultDisputed, DeliveryType.ResultSubmitted, DeliveryType.ResultReminder, DeliveryType.ResultSettled },
            t => Assert.True(handler.Handles(t)));
        Assert.False(handler.Handles(DeliveryType.CreateTableThread));
    }
}
