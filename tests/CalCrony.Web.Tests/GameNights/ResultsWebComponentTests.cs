using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using CalCrony.Contracts.GameNights;
using CalCrony.Web.Api;
using CalCrony.Web.Auth;
using CalCrony.Web.GameNights;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests.GameNights;

/// <summary>The results side of the website: the report form, result cards, result history, league standings and the
/// player profile. For each, what a member, an organiser and a moderator see; that names come from the name service;
/// and that no photo can be sent from the website.</summary>
public class ResultsWebComponentTests : TestContext
{
    private const long Guild = 77;
    private const long Me = 42;
    private const long Sam = 100;
    private static readonly Guid TableId = Guid.NewGuid();
    private static readonly Guid EventId = Guid.NewGuid();
    private static readonly Guid RootId = Guid.NewGuid();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, (HttpStatusCode Status, object? Body)> routes = [];
    private readonly List<(string Method, string Path, string Body)> requests = [];

    public ResultsWebComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped(_ => new GameNightsWebApiClient(
            new HttpClient(new RecordingHandler(routes, requests)) { BaseAddress = new Uri("http://localhost") }));
        Services.AddScoped(_ => new CalCronyWebApiClient(
            new HttpClient(new RecordingHandler(routes, requests)) { BaseAddress = new Uri("http://localhost") }));
        Services.AddSingleton<ITokenStore, InMemoryTokenStore>();
        Services.AddSingleton<JwtAuthenticationStateProvider>();
        Services.AddScoped(sp => new AuthApiClient(
            new HttpClient(new SessionHandler()) { BaseAddress = new Uri("http://localhost") },
            sp.GetRequiredService<ITokenStore>(),
            sp.GetRequiredService<JwtAuthenticationStateProvider>()));
        this.AddAuthorization();
    }

    // ---- arranging ----

    private static string Gn(string tail) => $"/guilds/{Guild}/game-nights{tail}";

    private void Tier(GameNightsTier tier) => routes[$"GET {Gn("/me")}"] = (HttpStatusCode.OK, new MyTierResponse(tier, true));

    private void Names(params (long Id, string Name)[] names) =>
        routes[$"GET {Gn("/names")}"] = (HttpStatusCode.OK, names.ToDictionary(n => n.Id.ToString(), n => n.Name));

    private async Task SignInAsync() => Assert.True(await Services.GetRequiredService<AuthApiClient>().TryRefreshAsync());

    private static GameDto Root(bool photoRequired = false) => new(RootId, true, new GameSpec(
        "root", "Root", GameResultType.RankedPlacement, 2, 4, 4, true, 32, 1000,
        [new("players", "Players", GameFieldKind.Player, GameFieldScope.Table, true),
         new("faction", "Faction", GameFieldKind.PickList, GameFieldScope.Player, true, true, ["Cats", "Birds", "Woodland"]),
         new("score", "Final score", GameFieldKind.Number, GameFieldScope.Player, true),
         new("map", "Map", GameFieldKind.Text, GameFieldScope.Table, false)], PhotoRequired: photoRequired));

    private static TableDto Table(TableStatus status = TableStatus.Open, long? thread = 4242) =>
        new(TableId, EventId, RootId, "Root", 1, 4, Sam, status, thread, GameNightsVisibility.Public,
            [new TableSeatDto(Sam, 0), new TableSeatDto(Me, 1)]);

    private void ArrangeReport(GameNightsTier tier, bool photoRequired = false, TableStatus status = TableStatus.Open)
    {
        Tier(tier);
        routes[$"GET {Gn($"/tables/{TableId}")}"] = (HttpStatusCode.OK, Table(status));
        routes[$"GET {Gn("/games")}"] = (HttpStatusCode.OK, new List<GameDto> { Root(photoRequired) });
        routes[$"GET {Gn("/guests")}"] = (HttpStatusCode.OK, new List<GuestDto> { new(Guid.NewGuid(), "Pat G") });
        Names((Sam, "Sam T"), (Me, "Alex P"));
    }

    private static ResultDto Result(
        ResultStatus status = ResultStatus.Pending, long[]? awaiting = null, long[]? unnamed = null, Guid? id = null) => new(
        id ?? Guid.NewGuid(), TableId, EventId, RootId, "Root", status, Sam, DateTimeOffset.UtcNow, null,
        [new ResultPlayerDto(Sam, null, "Sam T", 1, 31, new Dictionary<string, string> { ["faction"] = "Cats" }),
         new ResultPlayerDto(Me, null, "Alex P", 2, 22, new Dictionary<string, string> { ["faction"] = "Birds" })],
        new Dictionary<string, string> { ["map"] = "Autumn" }, [new ResultConfirmationDto(Sam, ConfirmationDecision.Confirm)],
        awaiting ?? [], unnamed ?? []);

    private IRenderedComponent<ResultCard> Card(ResultDto result, bool moderator = false, long user = Me) => Render<ResultCard>(p => p
        .Add(c => c.Result, result).Add(c => c.GuildId, Guild).Add(c => c.UserId, user).Add(c => c.IsModerator, moderator));

    private static IEnumerable<string> Labels(IRenderedComponent<ResultCard> cut) => cut.FindAll("button").Select(b => b.TextContent.Trim());

    // ---- the draft (pure) ----

    [Fact]
    public void The_draft_builds_the_same_submission_the_bot_sends_and_leaves_empty_answers_out()
    {
        var draft = new ReportDraft(Root().Spec);
        var sam = draft.AddMember(Sam, "Sam T");
        var me = draft.AddMember(Me, "Alex P");
        sam.Place = "1";
        sam.Values["faction"] = "Cats";
        sam.Values["score"] = " 31 ";
        me.Place = "2";
        me.Values["faction"] = "Birds";
        me.Values["score"] = "22";
        me.Values["map"] = "";
        draft.TableValues["map"] = "";

        var submission = draft.Build();

        Assert.Empty(draft.Validate());
        Assert.Equal(new int?[] { 1, 2 }, submission.Players.Select(p => p.Placement));
        Assert.Equal("31", submission.Players[0].Values!["score"]);
        Assert.DoesNotContain("map", submission.Players[1].Values!.Keys);
        Assert.Empty(submission.TableValues!);
    }

    [Fact]
    public void The_draft_uses_the_shared_validator_so_it_reports_what_the_api_would_refuse()
    {
        var draft = new ReportDraft(Root().Spec);
        draft.AddMember(Sam, "Sam T").Place = "1";
        draft.AddMember(Me, "Alex P").Place = "2";

        var problems = draft.Validate();

        Assert.Contains(problems, p => p.Field == "faction");
        Assert.Contains(problems, p => p.Field == "score");
    }

    [Fact]
    public void A_single_winner_game_places_only_the_winner_and_a_side_game_uses_one_and_two()
    {
        var single = new ReportDraft(Root().Spec with { ResultType = GameResultType.SingleWinner });
        var a = single.AddMember(Sam, "Sam T");
        single.AddMember(Me, "Alex P");
        single.Winner = a;
        Assert.Equal(new int?[] { 1, null }, single.Build().Players.Select(p => p.Placement));
        single.Remove(a);
        Assert.Null(single.Winner);

        var sides = new ReportDraft(Root().Spec with { ResultType = GameResultType.WinningSide });
        sides.AddMember(Sam, "Sam T").Place = "1";
        sides.AddMember(Me, "Alex P").Place = "2";
        Assert.Equal(new int?[] { 1, 2 }, sides.Build().Players.Select(p => p.Placement));
    }

    [Fact]
    public void No_request_the_website_can_send_carries_a_photo_or_a_file()
    {
        // The board photo stays in Discord: nothing in the report contracts can hold one, so the website cannot send one.
        var forbidden = new[] { "photo", "image", "file", "blob", "attachment", "upload", "url", "link", "bytes", "data" };
        var types = new[] { typeof(SubmitResultRequest), typeof(ResultSubmission), typeof(SubmittedPlayer), typeof(AnswerResultRequest), typeof(ResolveResultRequest) };

        foreach (var property in types.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)))
        {
            Assert.DoesNotContain(forbidden, word => property.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
            Assert.NotEqual(typeof(byte[]), property.PropertyType);
            Assert.NotEqual(typeof(Stream), property.PropertyType);
        }
    }

    // ---- the report page ----

    [Fact]
    public async Task The_report_form_lists_the_seated_players_by_name_and_has_no_file_upload()
    {
        ArrangeReport(GameNightsTier.Member);
        await SignInAsync();

        var cut = Render<ReportResultPage>(p => p.Add(c => c.GuildId, Guild).Add(c => c.TableId, TableId));

        cut.WaitForAssertion(() => Assert.Contains("Sam T", cut.Find("[data-testid=roster]").TextContent));
        Assert.Contains("Alex P (you)", cut.Find("[data-testid=roster]").TextContent);
        Assert.DoesNotContain("user 100", cut.Markup);
        Assert.Empty(cut.FindAll("input[type=file]"));
        Assert.Contains("this form has no photo upload", cut.Find("[data-testid=photo-note]").TextContent);
        Assert.Contains("https://discord.com/channels/77/4242", cut.Find("[data-testid=photo-note] a").GetAttribute("href"));
        Assert.DoesNotContain("this game asks for one", cut.Find("[data-testid=photo-note]").TextContent);
    }

    [Fact]
    public async Task A_game_that_requires_a_photo_says_so_but_still_takes_it_in_the_thread()
    {
        ArrangeReport(GameNightsTier.Member, photoRequired: true);
        await SignInAsync();

        var cut = Render<ReportResultPage>(p => p.Add(c => c.GuildId, Guild).Add(c => c.TableId, TableId));

        cut.WaitForAssertion(() => Assert.Contains("this game asks for one", cut.Find("[data-testid=photo-note]").TextContent));
        Assert.Empty(cut.FindAll("input[type=file]"));
    }

    [Fact]
    public async Task A_member_gets_no_guest_controls_on_the_report_form()
    {
        ArrangeReport(GameNightsTier.Member);
        await SignInAsync();

        var member = Render<ReportResultPage>(p => p.Add(c => c.GuildId, Guild).Add(c => c.TableId, TableId));

        member.WaitForAssertion(() => Assert.Contains("Who played", member.Markup));
        Assert.Empty(member.FindAll("[data-testid=guests]"));
        Assert.DoesNotContain(requests, r => r.Path.EndsWith("/guests"));
    }

    [Fact]
    public async Task An_organiser_can_add_and_pick_guests_on_the_report_form()
    {
        ArrangeReport(GameNightsTier.Organiser);
        await SignInAsync();

        var organiser = Render<ReportResultPage>(p => p.Add(c => c.GuildId, Guild).Add(c => c.TableId, TableId));

        organiser.WaitForAssertion(() => Assert.Contains("Add guest", organiser.Find("[data-testid=guests]").TextContent));
        Assert.Contains("Pat G", organiser.Find("[data-testid=guests]").TextContent);
        organiser.FindAll("[data-testid=guests] button").Single(b => b.TextContent.Contains("Pat G")).Click();
        organiser.WaitForAssertion(() => Assert.Contains("guest", organiser.Find("[data-testid=roster]").TextContent));
        Assert.Contains("Pat G", organiser.Find("[data-testid=roster]").TextContent);
    }

    [Fact]
    public async Task Submitting_stays_off_until_the_report_is_valid_then_sends_one_json_request_with_no_photo()
    {
        ArrangeReport(GameNightsTier.Member);
        await SignInAsync();
        routes[$"POST {Gn($"/tables/{TableId}/results")}"] = (HttpStatusCode.Created, Result(awaiting: [Sam]));
        var cut = Render<ReportResultPage>(p => p.Add(c => c.GuildId, Guild).Add(c => c.TableId, TableId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=players] .card")));
        Assert.True(cut.Find("[data-testid=submit]").HasAttribute("disabled"));
        Assert.NotEmpty(cut.FindAll("[data-testid=problems] li"));

        foreach (var (id, place, faction, score) in new[] { (Sam, "1", "Cats", "31"), (Me, "2", "Birds", "22") })
        {
            cut.Find($"#place-{id}").Input(place);
            cut.Find($"#faction-{id}").Change(faction);
            cut.Find($"#score-{id}").Input(score);
        }

        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=submit]").HasAttribute("disabled")));
        cut.Find("[data-testid=submit]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=photo-prompt]")));
        var sent = Assert.Single(requests, r => r.Method == "POST");
        Assert.Equal(Gn($"/tables/{TableId}/results"), sent.Path);
        Assert.Contains("\"placement\":1", sent.Body);
        Assert.Contains("\"faction\":\"Cats\"", sent.Body);
        Assert.DoesNotContain("photo", sent.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Waiting for Sam T", cut.Find("[data-testid=awaiting]").TextContent);
    }

    [Fact]
    public async Task A_member_who_did_not_play_cannot_report()
    {
        ArrangeReport(GameNightsTier.Member);
        await SignInAsync();
        var member = Render<ReportResultPage>(p => p.Add(c => c.GuildId, Guild).Add(c => c.TableId, TableId));
        member.WaitForAssertion(() => Assert.NotEmpty(member.FindAll("#played-42")));

        member.Find("#played-42").Change(false); // untick themselves

        member.WaitForAssertion(() => Assert.Contains("Only someone who played", member.Markup));
        Assert.True(member.Find("[data-testid=submit]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task An_organiser_can_report_a_table_they_did_not_play_at()
    {
        ArrangeReport(GameNightsTier.Organiser);
        await SignInAsync();
        var organiser = Render<ReportResultPage>(p => p.Add(c => c.GuildId, Guild).Add(c => c.TableId, TableId));
        organiser.WaitForAssertion(() => Assert.NotEmpty(organiser.FindAll("#played-42")));

        organiser.Find("#played-42").Change(false);

        organiser.WaitForAssertion(() => Assert.False(organiser.Find("#played-42").HasAttribute("checked")));
        Assert.DoesNotContain("Only someone who played", organiser.Markup);
    }

    [Fact]
    public async Task A_cancelled_table_takes_no_report()
    {
        ArrangeReport(GameNightsTier.Organiser, status: TableStatus.Cancelled);
        await SignInAsync();

        var cut = Render<ReportResultPage>(p => p.Add(c => c.GuildId, Guild).Add(c => c.TableId, TableId));

        cut.WaitForAssertion(() => Assert.Contains("table was cancelled", cut.Markup));
        Assert.Empty(cut.FindAll("[data-testid=submit]"));
    }

    // ---- result cards ----

    [Fact]
    public void A_card_shows_the_players_by_name_with_place_and_score_and_never_a_photo()
    {
        var cut = Card(Result(ResultStatus.Confirmed));

        var text = cut.Find("[data-testid=players]").TextContent;
        Assert.Contains("Sam T", text);
        Assert.Contains("31 pts", text);
        Assert.Contains("Faction: Cats", text);
        Assert.Contains("Map: Autumn", cut.Find("[data-testid=table-values]").TextContent);
        Assert.Equal("Confirmed", cut.Find("[data-testid=status]").TextContent.Trim());
        Assert.Empty(cut.FindAll("img, input[type=file]"));
        Assert.DoesNotContain("user 100", cut.Markup);
    }

    [Fact]
    public void A_player_who_still_has_to_answer_can_confirm_or_reject_and_nobody_else_can()
    {
        Names((Me, "Alex P"));
        var waiting = Result(awaiting: [Me]);

        Assert.Equal(["Confirm", "Reject"], Labels(Card(waiting)));
        Assert.Empty(Labels(Card(waiting, user: Sam)));            // already answered
        Assert.Empty(Labels(Card(Result(ResultStatus.Confirmed, awaiting: [Me]))));
        Assert.Contains("Waiting for you", Card(waiting).Find("[data-testid=awaiting]").TextContent);
    }

    [Fact]
    public void Only_a_moderator_gets_accept_and_void()
    {
        var disputed = Result(ResultStatus.Disputed);

        Assert.Empty(Labels(Card(disputed)));
        Assert.Equal(["Accept as reported", "Void"], Labels(Card(disputed, moderator: true)));
        Assert.Equal(["Void"], Labels(Card(Result(ResultStatus.Confirmed), moderator: true)));
        Assert.Empty(Labels(Card(Result(ResultStatus.Voided), moderator: true)));
    }

    [Fact]
    public void Confirming_asks_the_api_as_the_signed_in_player_and_shows_the_new_state()
    {
        var waiting = Result(awaiting: [Me]);
        routes[$"POST {Gn($"/results/{waiting.Id}/confirm")}"] = (HttpStatusCode.OK, waiting with { Status = ResultStatus.Confirmed, AwaitingUserIds = [] });
        ResultDto? changed = null;
        var cut = Render<ResultCard>(p => p
            .Add(c => c.Result, waiting).Add(c => c.GuildId, Guild).Add(c => c.UserId, Me)
            .Add(c => c.OnChanged, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<ResultDto>(this, r => changed = r)));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal(ResultStatus.Confirmed, changed?.Status));
        var sent = Assert.Single(requests, r => r.Method == "POST");
        Assert.Contains("\"userId\":null", sent.Body); // no id sent: the API means the signed-in member
    }

    [Fact]
    public void A_refused_answer_shows_the_reason()
    {
        var waiting = Result(awaiting: [Me]);
        routes[$"POST {Gn($"/results/{waiting.Id}/reject")}"] = (HttpStatusCode.Conflict, new { error = "This result has already been settled." });
        var cut = Card(waiting);

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reject").Click();

        cut.WaitForAssertion(() => Assert.Contains("already been settled", cut.Find(".alert-danger").TextContent));
    }

    [Fact]
    public void A_player_with_no_usable_nickname_is_nudged_to_fix_it()
    {
        Assert.Contains("Set your server nickname", Card(Result(unnamed: [Me])).Find("[data-testid=nickname-nudge]").TextContent);
        Assert.Empty(Card(Result(unnamed: [Sam])).FindAll("[data-testid=nickname-nudge]"));
    }

    // ---- result history ----

    [Fact]
    public async Task The_history_lists_results_puts_the_ones_waiting_for_you_first_and_gives_members_no_guest_controls()
    {
        Tier(GameNightsTier.Member);
        routes[$"GET {Gn("/games")}"] = (HttpStatusCode.OK, new List<GameDto> { Root() });
        routes[$"GET {Gn("/results")}"] = (HttpStatusCode.OK, new List<ResultDto> { Result(awaiting: [Me]), Result(ResultStatus.Confirmed) });
        Names((Me, "Alex P"));
        await SignInAsync();

        var cut = Render<GuildResults>(p => p.Add(c => c.GuildId, Guild));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=results] [data-testid=result-card]").Count));
        Assert.Single(cut.FindAll("[data-testid=waiting] [data-testid=result-card]"));
        Assert.Empty(cut.FindAll("[data-testid=guest-list]"));
        Assert.DoesNotContain(requests, r => r.Path.EndsWith("/guests"));
    }

    private GuestDto ArrangeHistoryWithGuest()
    {
        routes[$"GET {Gn("/games")}"] = (HttpStatusCode.OK, new List<GameDto> { Root() });
        routes[$"GET {Gn("/results")}"] = (HttpStatusCode.OK, new List<ResultDto>());
        var guest = new GuestDto(Guid.NewGuid(), "Pat G");
        routes[$"GET {Gn("/guests")}"] = (HttpStatusCode.OK, new List<GuestDto> { guest });
        return guest;
    }

    [Fact]
    public async Task An_organiser_sees_the_guest_list_but_cannot_erase_a_name()
    {
        ArrangeHistoryWithGuest();
        Tier(GameNightsTier.Organiser);
        await SignInAsync();

        var organiser = Render<GuildResults>(p => p.Add(c => c.GuildId, Guild));

        organiser.WaitForAssertion(() => Assert.Contains("Pat G", organiser.Find("[data-testid=guest-list]").TextContent));
        Assert.Empty(organiser.FindAll("[data-testid=guest-list] button"));
    }

    [Fact]
    public async Task A_moderator_can_erase_a_guests_name()
    {
        var guest = ArrangeHistoryWithGuest();
        Tier(GameNightsTier.Moderator);
        routes[$"DELETE {Gn($"/guests/{guest.Id}")}"] = (HttpStatusCode.NoContent, null);
        await SignInAsync();

        var moderator = Render<GuildResults>(p => p.Add(c => c.GuildId, Guild));
        moderator.WaitForAssertion(() => Assert.Single(moderator.FindAll("[data-testid=guest-list] button")));
        moderator.Find("[data-testid=guest-list] button").Click();

        moderator.WaitForAssertion(() => Assert.Contains("No guests", moderator.Find("[data-testid=guest-list]").TextContent));
        Assert.Contains(requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task The_history_says_so_when_game_nights_is_off()
    {
        await SignInAsync();

        var cut = Render<GuildResults>(p => p.Add(c => c.GuildId, Guild));

        cut.WaitForAssertion(() => Assert.Contains("isn't switched on", cut.Markup));
    }

    // ---- leagues ----

    private static LeagueStandingsDto Standings(params SeasonDto[] seasons) => new(
        RootId, "Root", null, "All time",
        [new StandingDto(1, Sam, null, "Sam T", 1016, 1016, 2), new StandingDto(2, null, Guid.NewGuid(), "Pat G", 984, 984, 2)],
        seasons, 2);

    private void ArrangeLeague(GameNightsTier tier, params SeasonDto[] seasons)
    {
        Tier(tier);
        routes[$"GET {Gn("/games")}"] = (HttpStatusCode.OK, new List<GameDto> { Root() });
        routes[$"GET {Gn($"/games/{RootId}/league")}"] = (HttpStatusCode.OK, Standings(seasons));
    }

    [Fact]
    public void A_member_sees_the_standings_with_names_and_links_to_player_pages_but_no_season_controls()
    {
        ArrangeLeague(GameNightsTier.Member);

        var cut = Render<GuildLeagues>(p => p.Add(c => c.GuildId, Guild));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=standings] tbody tr").Count));
        Assert.Contains("1016", cut.Find("[data-testid=standings]").TextContent);
        Assert.Equal($"/app/guilds/{Guild}/players/{Sam}", cut.Find("[data-testid=standings] a").GetAttribute("href"));
        Assert.Single(cut.FindAll("[data-testid=standings] a")); // the guest has no profile page
        Assert.Empty(cut.FindAll("[data-testid=season-admin]"));
    }

    private static readonly SeasonDto Autumn = new(Guid.NewGuid(), "Autumn", DateTimeOffset.UtcNow.AddDays(-3), null);

    [Fact]
    public void An_organiser_can_start_and_end_a_season_but_not_rebuild_ratings()
    {
        ArrangeLeague(GameNightsTier.Organiser, Autumn);

        var organiser = Render<GuildLeagues>(p => p.Add(c => c.GuildId, Guild));

        organiser.WaitForAssertion(() => Assert.NotEmpty(organiser.FindAll("[data-testid=season-admin]")));
        var labels = organiser.FindAll("[data-testid=season-admin] button").Select(b => b.TextContent.Trim()).ToList();
        Assert.Contains("Start a season now", labels);
        Assert.Contains("End \"Autumn\"", labels);
        Assert.DoesNotContain("Rebuild ratings", labels);
    }

    [Fact]
    public void A_moderator_can_also_rebuild_ratings()
    {
        ArrangeLeague(GameNightsTier.Moderator, Autumn);

        var moderator = Render<GuildLeagues>(p => p.Add(c => c.GuildId, Guild));

        moderator.WaitForAssertion(() => Assert.Contains("Rebuild ratings", moderator.Find("[data-testid=season-admin]").TextContent));
    }

    [Fact]
    public void Starting_a_season_sends_its_name_and_reloads_the_table()
    {
        ArrangeLeague(GameNightsTier.Organiser);
        routes[$"POST {Gn($"/games/{RootId}/league/seasons")}"] = (HttpStatusCode.Created, new SeasonDto(Guid.NewGuid(), "Winter", DateTimeOffset.UtcNow, null));
        var cut = Render<GuildLeagues>(p => p.Add(c => c.GuildId, Guild));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=season-admin]")));

        cut.Find("[data-testid=season-admin] input").Input("Winter");
        cut.FindAll("[data-testid=season-admin] button").Single(b => b.TextContent.Trim() == "Start a season now").Click();

        cut.WaitForAssertion(() => Assert.Single(requests, r => r.Method == "POST"));
        Assert.Contains("\"name\":\"Winter\"", requests.Single(r => r.Method == "POST").Body);
        Assert.Equal(2, requests.Count(r => r.Method == "GET" && r.Path.EndsWith("/league"))); // loaded, then reloaded
    }

    // ---- player profile ----

    [Fact]
    public async Task A_profile_shows_the_players_name_ratings_history_and_results()
    {
        Tier(GameNightsTier.Member);
        Names((Sam, "Sam T"));
        routes[$"GET {Gn("/leagues/me")}"] = (HttpStatusCode.OK, new List<MyRatingDto> { new(RootId, "Root", "All time", 1016, 1, 2, 2) });
        routes[$"GET {Gn($"/games/{RootId}/league/history")}"] = (HttpStatusCode.OK, new List<RatingHistoryEntryDto>
        {
            new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-7), 1, 1000, 1016),
            new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-1), 1, 1016, 1030.5),
        });
        routes[$"GET {Gn("/results")}"] = (HttpStatusCode.OK, new List<ResultDto> { Result(ResultStatus.Confirmed) });
        await SignInAsync();

        var cut = Render<PlayerProfilePage>(p => p.Add(c => c.GuildId, Guild).Add(c => c.UserId, Sam));

        cut.WaitForAssertion(() => Assert.Contains("Sam T", cut.Find("[data-testid=player-name]").TextContent));
        Assert.DoesNotContain("(you)", cut.Find("[data-testid=player-name]").TextContent);
        Assert.Contains("rank 1 of 2", cut.Find("[data-testid=rating]").TextContent);
        Assert.Equal(2, cut.FindAll("[data-testid=history] tbody tr").Count);
        Assert.Contains("+16", cut.Find("[data-testid=history]").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-testid=spark]"));
        Assert.Single(cut.FindAll("[data-testid=player-results] [data-testid=result-card]"));
        Assert.Contains(requests, r => r.Path == Gn("/leagues/me"));
    }

    // ---- the table page ----

    [Fact]
    public async Task The_table_page_links_to_the_report_form_and_lists_the_tables_results()
    {
        Tier(GameNightsTier.Member);
        routes[$"GET {Gn($"/tables/{TableId}")}"] = (HttpStatusCode.OK, Table());
        routes[$"GET {Gn($"/tables/{TableId}/results")}"] = (HttpStatusCode.OK, new List<ResultDto> { Result(ResultStatus.Confirmed) });
        Names((Sam, "Sam T"), (Me, "Alex P"));
        await SignInAsync();

        var cut = Render<TablePage>(p => p.Add(c => c.GuildId, Guild).Add(c => c.TableId, TableId));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=report-link]")));
        Assert.Equal($"/app/guilds/{Guild}/tables/{TableId}/report", cut.Find("[data-testid=report-link]").GetAttribute("href"));
        Assert.Single(cut.FindAll("[data-testid=table-results] [data-testid=result-card]"));
        Assert.Contains("Sam T", cut.Find("[data-testid=table-card]").TextContent); // seats by name, not "user 100"
        Assert.DoesNotContain("user 100", cut.Markup);
    }

    [Fact]
    public void The_tabs_include_leagues_and_results_when_game_nights_is_on()
    {
        Tier(GameNightsTier.Member);

        var cut = Render<GameNightsTabs>(p => p.Add(c => c.GuildId, Guild).Add(c => c.Active, "leagues"));

        cut.WaitForAssertion(() => Assert.Contains("Results", cut.Markup));
        Assert.Contains("/leagues", cut.Find("a.btn-primary").GetAttribute("href"));
        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == $"/app/guilds/{Guild}/results");
    }

    // ---- plumbing ----

    private sealed class SessionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var session = new CalCrony.Contracts.WebSessionResponse("token", DateTimeOffset.UtcNow.AddMinutes(10), Me, "Alex", null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(session, Web), Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RecordingHandler(
        Dictionary<string, (HttpStatusCode Status, object? Body)> routes, List<(string Method, string Path, string Body)> requests)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            requests.Add((request.Method.Method, path, body));
            if (!routes.TryGetValue($"{request.Method.Method} {path}", out var hit))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{\"error\":\"not found\"}", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(hit.Status)
            {
                Content = hit.Body is null ? new ByteArrayContent([]) : new StringContent(JsonSerializer.Serialize(hit.Body, Web), Encoding.UTF8, "application/json"),
            };
        }
    }
}
