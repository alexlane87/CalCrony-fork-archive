using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests.GameNights;

/// <summary>Pure: the name convention and the confirmation rule.</summary>
public class ResultRulesTests
{
    [Theory]
    [InlineData("Alex Pearson", "Alex P")]
    [InlineData("alex pearson", "Alex P")]
    [InlineData("ALEX PEARSON", "Alex P")]
    [InlineData("Alex P", "Alex P")]
    [InlineData("Alex P.", "Alex P")]
    [InlineData("  Alex   Pearson  ", "Alex P")]
    [InlineData("Mary-Jane Smith", "Mary-Jane S")]
    [InlineData("anne smith-jones", "Anne S")]
    [InlineData("Anne de Smith", "Anne S")]
    [InlineData("O'Neil Ryan", "O'Neil R")]
    [InlineData("Zoë Éclair", "Zoë É")]
    public void A_nickname_in_the_convention_gives_first_name_and_initial(string nickname, string expected) =>
        Assert.Equal(expected, DisplayNames.FromNickname(nickname));

    [Theory]
    [InlineData("Alex")]
    [InlineData("alex")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Alex P | Bristol")]
    [InlineData("Alex P 🎲")]
    [InlineData("Alex2 Pearson")]
    [InlineData("xX_Gamer_Xx")]
    [InlineData("-Alex Pearson")]
    public void A_single_name_or_anything_with_symbols_digits_or_emoji_does_not_match(string? nickname) =>
        Assert.Null(DisplayNames.FromNickname(nickname));

    [Fact]
    public void A_name_too_long_to_store_does_not_match() =>
        Assert.Null(DisplayNames.FromNickname(new string('a', 60) + " B"));

    [Theory]
    [InlineData("Sam T", "Sam T")]
    [InlineData("sam turner", "Sam T")]
    [InlineData("Anne de Smith", null)] // a typed name is given as two words
    [InlineData("Sam", null)]
    public void A_typed_name_is_tidied_only_if_it_is_two_words(string name, string? expected) =>
        Assert.Equal(expected, DisplayNames.Tidy(name));

    [Fact]
    public void Everyone_who_played_must_confirm_when_the_rule_is_all()
    {
        Assert.False(ResultRules.IsConfirmed(ConfirmMode.All, [1, 2, 3], [1]));
        Assert.False(ResultRules.IsConfirmed(ConfirmMode.All, [1, 2, 3], [1, 2]));
        Assert.True(ResultRules.IsConfirmed(ConfirmMode.All, [1, 2, 3], [1, 2, 3]));
        Assert.True(ResultRules.IsConfirmed(ConfirmMode.All, [1, 2, 3], [3, 2, 1, 99])); // someone else's answer changes nothing
    }

    [Fact]
    public void More_than_half_must_confirm_when_the_rule_is_majority()
    {
        Assert.False(ResultRules.IsConfirmed(ConfirmMode.Majority, [1, 2, 3, 4], [1, 2])); // exactly half is not a majority
        Assert.True(ResultRules.IsConfirmed(ConfirmMode.Majority, [1, 2, 3, 4], [1, 2, 3]));
        Assert.True(ResultRules.IsConfirmed(ConfirmMode.Majority, [1, 2, 3], [1, 2]));
        Assert.False(ResultRules.IsConfirmed(ConfirmMode.Majority, [1], []));
    }

    [Fact]
    public void A_table_with_no_members_only_guests_is_confirmed_and_duplicates_do_not_count_twice()
    {
        Assert.True(ResultRules.IsConfirmed(ConfirmMode.All, [], []));
        Assert.False(ResultRules.IsConfirmed(ConfirmMode.All, [1, 1, 2], [1]));
    }
}

/// <summary>The default confirm rule is "all"; this fixture switches to "majority".</summary>
public sealed class GameNightsMajorityFixture : WebAuthFixture
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("GameNights:Enabled", "true");
        builder.UseSetting("GameNights:ConfirmMode", "Majority");
    }
}

/// <summary>Step 7 over HTTP: reporting, confirming, disputing, the moderator's say, names and guests, and the
/// privacy guarantees (no photo, no raw names). A web manager is Admin tier; plain web members are the players.</summary>
public class GameNightsResultApiTests(GameNightsFixture fixture) : IClassFixture<GameNightsFixture>
{
    private HttpClient Bot => fixture.Client;

    private static long nextGuild = 63000;

    private static long NewGuildId() => Interlocked.Increment(ref nextGuild);

    private sealed record Player(HttpClient Client, long Id);

    private sealed record Night(long Guild, HttpClient Admin, Player P1, Player P2, Player P3, GameDto Root, GameDto Arcs, TableDto RootTable, TableDto ArcsTable);

    private string Results(Night n, TableDto table, string tail = "") => $"/guilds/{n.Guild}/game-nights/tables/{table.Id}/results{tail}";

    private string Result(Night n, Guid id, string tail) => $"/guilds/{n.Guild}/game-nights/results/{id}{tail}";

    private static SubmittedPlayer Root(long id, int place, string faction, int score) => new(id, null, place,
        new Dictionary<string, string> { ["faction"] = faction, ["score"] = score.ToString() });

    private static SubmittedPlayer Arcs(long id, int place, string colour, int score) => new(id, null, place,
        new Dictionary<string, string> { ["colour"] = colour, ["score"] = score.ToString() });

    private static ResultSubmission RootReport(Night n, params (Player p, int place, string faction, int score)[] who) =>
        new([.. who.Select(w => Root(w.p.Id, w.place, w.faction, w.score))], new Dictionary<string, string> { ["map"] = "Autumn", ["deck"] = "Standard" });

    private static ResultSubmission ThreeWayRoot(Night n) => RootReport(n,
        (n.P1, 1, "Marquise de Cat", 30), (n.P2, 2, "Eyrie Dynasties", 25), (n.P3, 3, "Vagabond", 12));

    // ---- the lifecycle ----

    [Fact]
    public async Task A_root_result_is_reported_confirmed_by_the_others_and_marks_the_table_played()
    {
        var n = await ArrangeAsync();
        await NameAsync(n, (n.P1.Id, "Alex P"), (n.P2.Id, "Bea W"), (n.P3.Id, "Cal T"));

        var reported = await n.P1.Client.PostAsJsonAsync(Results(n, n.RootTable), new SubmitResultRequest(ThreeWayRoot(n)));
        Assert.Equal(HttpStatusCode.Created, reported.StatusCode);
        var pending = (await reported.Content.ReadFromJsonAsync<ResultDto>())!;
        Assert.Equal(ResultStatus.Pending, pending.Status);
        Assert.Equal([n.P2.Id, n.P3.Id], pending.AwaitingUserIds.Order()); // the reporter counts as confirming
        Assert.Equal(["Alex P", "Bea W", "Cal T"], pending.Players.Select(p => p.DisplayName));
        Assert.Equal((1, 30L, "Marquise de Cat"), (pending.Players[0].Placement, pending.Players[0].Score, pending.Players[0].Values["faction"]));
        Assert.Equal("Autumn", pending.TableValues["map"]);

        Assert.Equal(ResultStatus.Pending, await Answer(n.P2, n, pending.Id, "confirm"));
        Assert.Equal(ResultStatus.Confirmed, await Answer(n.P3, n, pending.Id, "confirm"));

        var after = (await n.P1.Client.GetFromJsonAsync<ResultDto>(Result(n, pending.Id, "")))!;
        Assert.NotNull(after.ConfirmedAtUtc);
        Assert.Empty(after.AwaitingUserIds);
        Assert.Equal(TableStatus.Played, (await TableAsync(n, n.RootTable)).Status);
    }

    [Fact]
    public async Task An_arcs_result_with_its_own_questions_goes_the_same_way()
    {
        var n = await ArrangeAsync();
        var report = new ResultSubmission([Arcs(n.P1.Id, 1, "Red", 28), Arcs(n.P2.Id, 2, "Blue", 21), Arcs(n.P3.Id, 3, "White", 17)],
            new Dictionary<string, string> { ["round_ended"] = "5" });

        var pending = await SubmitAsync(n.P2, n, n.ArcsTable, report);
        await Answer(n.P1, n, pending.Id, "confirm");
        await Answer(n.P3, n, pending.Id, "confirm");

        var done = (await n.P1.Client.GetFromJsonAsync<ResultDto>(Result(n, pending.Id, "")))!;
        Assert.Equal(ResultStatus.Confirmed, done.Status);
        Assert.Equal("5", done.TableValues["round_ended"]);
        Assert.Equal(["Red", "Blue", "White"], done.Players.Select(p => p.Values["colour"]));
    }

    [Fact]
    public async Task The_recent_results_list_is_newest_first_and_filters_by_game_and_status()
    {
        var n = await ArrangeAsync();
        var root = await SubmitAsync(n.P1, n, n.RootTable, ThreeWayRoot(n));
        var arcs = await SubmitAsync(n.P2, n, n.ArcsTable, new ResultSubmission(
            [Arcs(n.P1.Id, 1, "Red", 28), Arcs(n.P2.Id, 2, "Blue", 21), Arcs(n.P3.Id, 3, "White", 17)], new Dictionary<string, string> { ["round_ended"] = "5" }));
        var list = $"/guilds/{n.Guild}/game-nights/results";

        var all = (await n.P3.Client.GetFromJsonAsync<List<ResultDto>>(list))!;
        Assert.Equal([arcs.Id, root.Id], all.Select(r => r.Id));

        var onlyRoot = (await n.P3.Client.GetFromJsonAsync<List<ResultDto>>($"{list}?gameId={n.Root.Id}"))!;
        Assert.Equal([root.Id], onlyRoot.Select(r => r.Id));

        Assert.Empty((await n.P3.Client.GetFromJsonAsync<List<ResultDto>>($"{list}?status={(int)ResultStatus.Confirmed}"))!);
        await Answer(n.P2, n, root.Id, "confirm");
        await Answer(n.P3, n, root.Id, "confirm");
        var confirmed = (await n.P3.Client.GetFromJsonAsync<List<ResultDto>>($"{list}?status={(int)ResultStatus.Confirmed}"))!;
        Assert.Equal([root.Id], confirmed.Select(r => r.Id));

        var mine = (await n.P3.Client.GetFromJsonAsync<List<ResultDto>>($"{list}?userId={n.P3.Id}&take=1"))!;
        Assert.Single(mine); // take caps the page
    }

    [Fact]
    public async Task A_result_from_another_server_never_shows_in_this_servers_list()
    {
        var n = await ArrangeAsync();
        var other = await ArrangeAsync();
        await SubmitAsync(other.P1, other, other.RootTable, ThreeWayRoot(other));

        var here = (await n.P1.Client.GetFromJsonAsync<List<ResultDto>>($"/guilds/{n.Guild}/game-nights/results"))!;

        Assert.Empty(here);
    }

    [Fact]
    public async Task A_rejection_disputes_it_tells_the_organisers_once_and_a_moderator_settles_it()
    {
        var n = await ArrangeAsync();
        await n.Admin.PutAsJsonAsync($"/guilds/{n.Guild}/game-nights/tier-roles", new PutTierRolesRequest([new(9100, GameNightsTier.Organiser)]));
        var pending = await SubmitAsync(n.P1, n, n.RootTable, ThreeWayRoot(n));

        Assert.Equal(ResultStatus.Disputed, await Answer(n.P2, n, pending.Id, "reject"));
        Assert.Equal(ResultStatus.Disputed, await Answer(n.P3, n, pending.Id, "reject")); // another rejection does not tell them twice

        var notes = await DeliveriesAsync(DeliveryType.ResultDisputed, pending.Id);
        var payload = JsonSerializer.Deserialize<ResultDisputedPayload>(Assert.Single(notes).PayloadJson)!;
        Assert.Equal((n.P2.Id, n.RootTable.Id), (payload.RejectedByUserId, payload.TableId));
        Assert.Equal([9100L], payload.NotifyRoleIds);

        // A confirm can't override a dispute; a moderator can settle it.
        Assert.Equal(HttpStatusCode.Conflict, (await n.P1.Client.PostAsJsonAsync(Result(n, pending.Id, "/confirm"), new AnswerResultRequest())).StatusCode);
        var settled = await n.Admin.PostAsJsonAsync(Result(n, pending.Id, "/resolve"), new ResolveResultRequest(ResolveAction.Confirm));
        Assert.Equal(ResultStatus.Confirmed, (await settled.Content.ReadFromJsonAsync<ResultDto>())!.Status);
        Assert.Equal(TableStatus.Played, (await TableAsync(n, n.RootTable)).Status);
    }

    [Fact]
    public async Task Voiding_a_result_frees_the_table_to_report_again_and_a_plain_member_cannot_void()
    {
        var n = await ArrangeAsync();
        var pending = await SubmitAsync(n.P1, n, n.RootTable, ThreeWayRoot(n));
        await Answer(n.P2, n, pending.Id, "confirm");
        await Answer(n.P3, n, pending.Id, "confirm");
        Assert.Equal(HttpStatusCode.Conflict, (await n.P1.Client.PostAsJsonAsync(Results(n, n.RootTable), new SubmitResultRequest(ThreeWayRoot(n)))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await n.P1.Client.PostAsJsonAsync(Result(n, pending.Id, "/resolve"), new ResolveResultRequest(ResolveAction.Void))).StatusCode);
        var voided = await n.Admin.PostAsJsonAsync(Result(n, pending.Id, "/resolve"), new ResolveResultRequest(ResolveAction.Void));
        Assert.Equal(ResultStatus.Voided, (await voided.Content.ReadFromJsonAsync<ResultDto>())!.Status);
        Assert.Equal(TableStatus.Open, (await TableAsync(n, n.RootTable)).Status);

        var again = await n.P1.Client.PostAsJsonAsync(Results(n, n.RootTable), new SubmitResultRequest(ThreeWayRoot(n)));
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal(2, (await n.P1.Client.GetFromJsonAsync<List<ResultDto>>(Results(n, n.RootTable)))!.Count);
    }

    [Fact]
    public async Task While_one_is_waiting_the_table_cannot_report_another()
    {
        var n = await ArrangeAsync();
        await SubmitAsync(n.P1, n, n.RootTable, ThreeWayRoot(n));

        var second = await n.P2.Client.PostAsJsonAsync(Results(n, n.RootTable), new SubmitResultRequest(ThreeWayRoot(n)));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("waiting to be confirmed", (await second.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    // ---- validation and who may report or answer ----

    [Fact]
    public async Task A_report_missing_required_answers_or_breaking_the_game_rules_is_refused_with_the_reasons()
    {
        var n = await ArrangeAsync();
        var noFaction = new ResultSubmission(
        [
            new(n.P1.Id, null, 1, new Dictionary<string, string> { ["score"] = "9" }),
            Root(n.P2.Id, 2, "Vagabond", 3),
        ]);

        var response = await n.P1.Client.PostAsJsonAsync(Results(n, n.RootTable), new SubmitResultRequest(noFaction));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Faction for player 1 is required", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);

        var sameFaction = RootReport(n, (n.P1, 1, "Vagabond", 9), (n.P2, 2, "Vagabond", 3));
        Assert.Equal(HttpStatusCode.BadRequest, (await n.P1.Client.PostAsJsonAsync(Results(n, n.RootTable), new SubmitResultRequest(sameFaction))).StatusCode);
        var tooFew = RootReport(n, (n.P1, 1, "Vagabond", 9));
        Assert.Equal(HttpStatusCode.BadRequest, (await n.P1.Client.PostAsJsonAsync(Results(n, n.RootTable), new SubmitResultRequest(tooFew))).StatusCode);
        Assert.Empty(await n.P1.Client.GetFromJsonAsync<List<ResultDto>>(Results(n, n.RootTable))!); // nothing was stored
    }

    [Fact]
    public async Task Only_someone_who_played_or_an_organiser_reports_and_only_players_answer()
    {
        var n = await ArrangeAsync();
        var outsider = await fixture.LoginAsync(n.Guild * 1000 + 9, (n.Guild, "G", false));
        var twoPlayers = RootReport(n, (n.P1, 1, "Marquise de Cat", 20), (n.P2, 2, "Vagabond", 10));

        Assert.Equal(HttpStatusCode.Forbidden, (await n.P3.Client.PostAsJsonAsync(Results(n, n.RootTable), new SubmitResultRequest(twoPlayers))).StatusCode);

        var byOrganiser = await n.Admin.PostAsJsonAsync(Results(n, n.RootTable), new SubmitResultRequest(twoPlayers));
        Assert.Equal(HttpStatusCode.Created, byOrganiser.StatusCode);
        var result = (await byOrganiser.Content.ReadFromJsonAsync<ResultDto>())!;
        // The organiser who reported is not a player, so both players still have to confirm.
        Assert.Equal([n.P1.Id, n.P2.Id], result.AwaitingUserIds.Order());
        Assert.Equal(HttpStatusCode.Forbidden, (await n.P3.Client.PostAsJsonAsync(Result(n, result.Id, "/confirm"), new AnswerResultRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Client.PostAsJsonAsync(Result(n, result.Id, "/reject"), new AnswerResultRequest())).StatusCode);
    }

    [Fact]
    public async Task The_bot_reports_and_answers_for_named_members_and_must_name_them()
    {
        var n = await ArrangeAsync();

        var anonymous = await Bot.PostAsJsonAsync(Results(n, n.RootTable), new SubmitResultRequest(ThreeWayRoot(n)));
        Assert.Equal(HttpStatusCode.BadRequest, anonymous.StatusCode);

        var created = await Bot.PostAsJsonAsync(Results(n, n.RootTable), new SubmitResultRequest(ThreeWayRoot(n), ReporterUserId: n.P1.Id));
        var result = (await created.Content.ReadFromJsonAsync<ResultDto>())!;
        Assert.Equal(HttpStatusCode.BadRequest, (await Bot.PostAsJsonAsync(Result(n, result.Id, "/confirm"), new AnswerResultRequest())).StatusCode);
        var answered = await Bot.PostAsJsonAsync(Result(n, result.Id, "/confirm"), new AnswerResultRequest(n.P2.Id));
        Assert.Equal([n.P3.Id], (await answered.Content.ReadFromJsonAsync<ResultDto>())!.AwaitingUserIds);
    }

    [Fact]
    public async Task A_result_with_one_member_and_guests_is_confirmed_at_once()
    {
        var n = await ArrangeAsync();
        var guest = (await (await n.Admin.PostAsJsonAsync($"/guilds/{n.Guild}/game-nights/guests", new CreateGuestRequest("sam turner")))
            .Content.ReadFromJsonAsync<GuestDto>())!;
        Assert.Equal("Sam T", guest.DisplayName);
        var report = new ResultSubmission(
        [
            Root(n.P1.Id, 1, "Marquise de Cat", 30),
            new(null, guest.Id, 2, new Dictionary<string, string> { ["faction"] = "Vagabond", ["score"] = "14" }),
        ]);

        var result = await SubmitAsync(n.P1, n, n.RootTable, report);

        Assert.Equal(ResultStatus.Confirmed, result.Status);
        Assert.Equal("Sam T", result.Players.Single(p => p.GuestId is not null).DisplayName);
        Assert.Equal(TableStatus.Played, (await TableAsync(n, n.RootTable)).Status);
    }

    // ---- names ----

    [Fact]
    public async Task Names_come_from_the_bots_first_l_snapshots_and_a_one_off_name_is_never_stored()
    {
        var n = await ArrangeAsync();
        await NameAsync(n, (n.P1.Id, "Alex P"), (n.P3.Id, "Cal T")); // P2 has no usable name

        var created = await n.P1.Client.PostAsJsonAsync(Results(n, n.RootTable), new SubmitResultRequest(
            ThreeWayRoot(n), NameOverrides: new Dictionary<long, string> { [n.P2.Id] = "bea west" }));
        var result = (await created.Content.ReadFromJsonAsync<ResultDto>())!;

        Assert.Equal("Bea W", result.Players.Single(p => p.UserId == n.P2.Id).DisplayName); // used for this response
        Assert.Empty(result.UnnamedUserIds);

        // Read back later: it was not kept against the member, who is "Unnamed player" and flagged for a nudge.
        var later = (await n.P1.Client.GetFromJsonAsync<ResultDto>(Result(n, result.Id, "")))!;
        Assert.Equal(DisplayNames.UnnamedPlayer, later.Players.Single(p => p.UserId == n.P2.Id).DisplayName);
        Assert.Equal([n.P2.Id], later.UnnamedUserIds);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        Assert.False(await db.GameNightMemberNames.AnyAsync(m => m.GuildId == n.Guild && m.UserId == n.P2.Id));
    }

    [Fact]
    public async Task A_one_off_name_that_is_not_in_the_convention_is_refused()
    {
        var n = await ArrangeAsync();

        var response = await n.P1.Client.PostAsJsonAsync(Results(n, n.RootTable), new SubmitResultRequest(
            ThreeWayRoot(n), NameOverrides: new Dictionary<long, string> { [n.P2.Id] = "Bea | Bristol" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_member_who_left_shows_as_former_member_and_names_can_be_read_in_bulk()
    {
        var n = await ArrangeAsync();
        await NameAsync(n, (n.P1.Id, "Alex P"));
        (await Bot.PutAsJsonAsync($"/guilds/{n.Guild}/game-nights/member-names",
            new PutMemberNamesRequest([new(n.P2.Id, "Bea W", Left: true)]))).EnsureSuccessStatusCode();
        var result = await SubmitAsync(n.P1, n, n.RootTable, ThreeWayRoot(n));

        Assert.Equal(DisplayNames.FormerMember, result.Players.Single(p => p.UserId == n.P2.Id).DisplayName);

        var names = await n.P3.Client.GetFromJsonAsync<Dictionary<string, string>>(
            $"/guilds/{n.Guild}/game-nights/names?ids={n.P1.Id},{n.P2.Id},{n.P3.Id}");
        Assert.Equal("Alex P", names![n.P1.Id.ToString()]);
        Assert.Equal(DisplayNames.FormerMember, names[n.P2.Id.ToString()]);
        Assert.Equal(DisplayNames.UnnamedPlayer, names[n.P3.Id.ToString()]);
    }

    [Fact]
    public async Task The_bot_is_told_who_to_look_up_and_only_the_bot_can_say_names()
    {
        var n = await ArrangeAsync();
        await n.Admin.PostAsJsonAsync($"/guilds/{n.Guild}/game-nights/tables/{n.RootTable.Id}/seats", new SeatRequest(n.P1.Id));
        await n.Admin.PostAsJsonAsync($"/guilds/{n.Guild}/game-nights/tables/{n.RootTable.Id}/seats", new SeatRequest(n.P2.Id));
        var wanted = $"/guilds/{n.Guild}/game-nights/member-names/wanted";

        var first = await Bot.GetFromJsonAsync<WantedNamesResponse>(wanted);
        Assert.Equal([n.P1.Id, n.P2.Id], first!.UserIds.Order());

        await NameAsync(n, (n.P1.Id, "Alex P")); // fresh and matched: not wanted again
        Assert.Equal([n.P2.Id], (await Bot.GetFromJsonAsync<WantedNamesResponse>(wanted))!.UserIds);

        Assert.Equal(HttpStatusCode.Forbidden, (await n.Admin.GetAsync(wanted)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await n.Admin.PutAsJsonAsync($"/guilds/{n.Guild}/game-nights/member-names",
            new PutMemberNamesRequest([new(n.P1.Id, "Alex P")]))).StatusCode);
    }

    [Fact]
    public async Task Only_the_derived_first_l_can_be_stored_never_a_raw_nickname()
    {
        var n = await ArrangeAsync();

        foreach (var raw in new[] { "Alex Pearson", "alex p", "xX_Alex_Xx", "Alex P | Bristol" })
        {
            var response = await Bot.PutAsJsonAsync($"/guilds/{n.Guild}/game-nights/member-names",
                new PutMemberNamesRequest([new(n.P1.Id, raw)]));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // "no match" is sent as null, and that is fine.
        Assert.Equal(HttpStatusCode.NoContent,
            (await Bot.PutAsJsonAsync($"/guilds/{n.Guild}/game-nights/member-names", new PutMemberNamesRequest([new(n.P1.Id, null)]))).StatusCode);
    }

    // ---- guests and privacy ----

    [Fact]
    public async Task Guests_are_organiser_managed_and_a_moderator_can_erase_the_name()
    {
        var n = await ArrangeAsync();
        var guests = $"/guilds/{n.Guild}/game-nights/guests";

        Assert.Equal(HttpStatusCode.Forbidden, (await n.P1.Client.PostAsJsonAsync(guests, new CreateGuestRequest("Sam T"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await n.Admin.PostAsJsonAsync(guests, new CreateGuestRequest("Sam"))).StatusCode);
        var guest = (await (await n.Admin.PostAsJsonAsync(guests, new CreateGuestRequest("Sam T"))).Content.ReadFromJsonAsync<GuestDto>())!;
        var report = new ResultSubmission([Root(n.P1.Id, 1, "Marquise de Cat", 30),
            new(null, guest.Id, 2, new Dictionary<string, string> { ["faction"] = "Vagabond", ["score"] = "14" })]);
        var result = await SubmitAsync(n.P1, n, n.RootTable, report);
        Assert.Single(await n.Admin.GetFromJsonAsync<List<GuestDto>>(guests)!);

        Assert.Equal(HttpStatusCode.NoContent, (await n.Admin.DeleteAsync($"{guests}/{guest.Id}")).StatusCode);

        var after = (await n.P1.Client.GetFromJsonAsync<ResultDto>(Result(n, result.Id, "")))!;
        Assert.Equal(DisplayNames.FormerGuest, after.Players.Single(p => p.GuestId is not null).DisplayName);
        Assert.Empty(await n.Admin.GetFromJsonAsync<List<GuestDto>>(guests)!);
        // An erased guest can't be put on a new report.
        (await n.Admin.PostAsJsonAsync(Result(n, result.Id, "/resolve"), new ResolveResultRequest(ResolveAction.Void))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await n.P1.Client.PostAsJsonAsync(Results(n, n.RootTable), new SubmitResultRequest(report))).StatusCode);
    }

    [Fact]
    public async Task The_database_has_no_place_for_a_photo_a_link_or_a_raw_name()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();

        var columns = await db.Database.SqlQueryRaw<string>(
            """SELECT lower(table_name || '.' || column_name) AS "Value" FROM information_schema.columns WHERE table_name LIKE 'GameNight%'""")
            .ToListAsync();

        Assert.NotEmpty(columns);
        var forbidden = new[] { "photo", "image", "picture", "url", "link", "blob", "file", "nick", "username", "avatar", "realname" };
        // The one allowed exception is a SETTING: whether a game's report must say the photo was posted (a yes/no on the game).
        Assert.Empty(columns.Where(c => c != "gamenightgames.photorequired" && forbidden.Any(f => c.Split('.')[1].Contains(f))));
        // The only column that holds a person's name is the derived First L (and a guest's typed name).
        Assert.Equal(
            ["gamenightguests.displayname", "gamenightmembernames.displayname"],
            columns.Where(c => c.Split('.')[1].Contains("name") && c.Contains("display")).Order());
    }

    [Fact]
    public async Task An_erased_guests_name_and_a_members_raw_nickname_are_found_nowhere_in_the_database()
    {
        var n = await ArrangeAsync();
        var guests = $"/guilds/{n.Guild}/game-nights/guests";
        // Distinctive words, so a hit anywhere in any table can only be ours.
        var guest = (await (await n.Admin.PostAsJsonAsync(guests, new CreateGuestRequest("yolanda zxqv"))).Content.ReadFromJsonAsync<GuestDto>())!;
        Assert.Equal("Yolanda Z", guest.DisplayName);
        await NameAsync(n, (n.P1.Id, "Alex P"));
        var rawNickname = await Bot.PutAsJsonAsync($"/guilds/{n.Guild}/game-nights/member-names",
            new PutMemberNamesRequest([new MemberNameDto(n.P2.Id, "Wolfgang Hrzxqv")]));
        Assert.Equal(HttpStatusCode.BadRequest, rawNickname.StatusCode); // only the derived First L may be stored
        var result = await SubmitAsync(n.P1, n, n.RootTable, new ResultSubmission([Root(n.P1.Id, 1, "Marquise de Cat", 30),
            new(null, guest.Id, 2, new Dictionary<string, string> { ["faction"] = "Vagabond", ["score"] = "14" })]));

        Assert.True(await OccurrencesAsync("Yolanda") >= 1); // it is stored (the guest's own row), so the scan can find it
        Assert.Equal(0, await OccurrencesAsync("Hrzxqv"));  // a raw nickname never gets in
        Assert.Equal(0, await OccurrencesAsync("zxqv"));    // nor the surname behind an initial

        Assert.Equal(HttpStatusCode.NoContent, (await n.Admin.DeleteAsync($"{guests}/{guest.Id}")).StatusCode);

        Assert.Equal(0, await OccurrencesAsync("Yolanda")); // gone from every text and JSON column of every table
        Assert.Equal(DisplayNames.FormerGuest, (await n.P1.Client.GetFromJsonAsync<ResultDto>(Result(n, result.Id, "")))!.Players.Single(p => p.GuestId is not null).DisplayName);
    }

    [Fact]
    public async Task No_table_that_holds_game_nights_data_can_store_raw_bytes()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();

        var binary = await db.Database.SqlQueryRaw<string>(
            """SELECT table_name || '.' || column_name AS "Value" FROM information_schema.columns WHERE table_schema = 'public' AND table_name LIKE 'GameNight%' AND data_type = 'bytea'""")
            .ToListAsync();

        Assert.Empty(binary); // no photo or file could be written even by mistake
    }

    /// <summary>How many cells, across every text and JSON column of every table, contain the text (case-insensitive).</summary>
    private async Task<int> OccurrencesAsync(string needle)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var columns = await db.Database.SqlQueryRaw<string>(
            """SELECT table_name || '|' || column_name AS "Value" FROM information_schema.columns WHERE table_schema = 'public' AND data_type IN ('text', 'character varying', 'json', 'jsonb')""")
            .ToListAsync();

        var total = 0;
        foreach (var column in columns)
        {
            // Table and column names come from the database's own catalogue, never from input.
            var parts = column.Split('|');
            total += (await db.Database.SqlQueryRaw<int>(
                $"SELECT count(*)::int AS \"Value\" FROM \"{parts[0]}\" WHERE \"{parts[1]}\"::text ILIKE {{0}}", $"%{needle}%").ToListAsync()).Single();
        }

        return total;
    }

    // ---- helpers ----

    private async Task<Night> ArrangeAsync()
    {
        var guild = NewGuildId();
        (await Bot.PutAsJsonAsync($"/guilds/{guild}/presence", new GuildPresenceRequest(true, "Results test guild"))).EnsureSuccessStatusCode();
        var (admin, _) = await fixture.LoginAsync(guild * 1000 + 1, (guild, "G", true));
        var one = await fixture.LoginAsync(guild * 1000 + 2, (guild, "G", false));
        var two = await fixture.LoginAsync(guild * 1000 + 3, (guild, "G", false));
        var three = await fixture.LoginAsync(guild * 1000 + 4, (guild, "G", false));
        (await admin.PostAsync($"/guilds/{guild}/game-nights/games/seed", null)).EnsureSuccessStatusCode();
        var games = (await admin.GetFromJsonAsync<List<GameDto>>($"/guilds/{guild}/game-nights/games"))!;
        var root = games.Single(g => g.Spec.Key == "root");
        var arcs = games.Single(g => g.Spec.Key == "arcs");
        var ev = await Bot.PostAsJsonAsync($"/guilds/{guild}/events", new CreateEventRequest(9000, "Game night", "in 3 hours", 64000 + guild % 1000));
        var eventId = (await ev.Content.ReadFromJsonAsync<EventDto>())!.Id;
        async Task<TableDto> Open(GameDto game) => (await (await admin.PostAsJsonAsync(
            $"/guilds/{guild}/game-nights/events/{eventId}/tables", new CreateTableRequest(game.Id))).Content.ReadFromJsonAsync<TableDto>())!;
        return new Night(guild, admin, new Player(one.Client, one.Session.UserId), new Player(two.Client, two.Session.UserId),
            new Player(three.Client, three.Session.UserId), root, arcs, await Open(root), await Open(arcs));
    }

    private async Task NameAsync(Night n, params (long Id, string Name)[] names) =>
        (await Bot.PutAsJsonAsync($"/guilds/{n.Guild}/game-nights/member-names",
            new PutMemberNamesRequest([.. names.Select(x => new MemberNameDto(x.Id, x.Name))]))).EnsureSuccessStatusCode();

    private async Task<ResultDto> SubmitAsync(Player by, Night n, TableDto table, ResultSubmission report)
    {
        var response = await by.Client.PostAsJsonAsync(Results(n, table), new SubmitResultRequest(report));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ResultDto>())!;
    }

    private async Task<ResultStatus> Answer(Player by, Night n, Guid resultId, string what)
    {
        var response = await by.Client.PostAsJsonAsync(Result(n, resultId, $"/{what}"), new AnswerResultRequest());
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ResultDto>())!.Status;
    }

    private async Task<TableDto> TableAsync(Night n, TableDto table) =>
        (await n.Admin.GetFromJsonAsync<TableDto>($"/guilds/{n.Guild}/game-nights/tables/{table.Id}"))!;

    private async Task<List<Delivery>> DeliveriesAsync(DeliveryType type, Guid resultId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var all = await db.Deliveries.AsNoTracking().Where(d => d.Type == type).OrderBy(d => d.CreatedAt).ToListAsync();
        return [.. all.Where(d => d.PayloadJson.Contains(resultId.ToString(), StringComparison.OrdinalIgnoreCase))];
    }
}

/// <summary>The configurable rule: with "majority", more than half of the members who played is enough.</summary>
public class GameNightsMajorityTests(GameNightsMajorityFixture fixture) : IClassFixture<GameNightsMajorityFixture>
{
    [Fact]
    public async Task With_the_majority_rule_two_of_three_confirming_settles_it()
    {
        var guild = 65001L;
        await fixture.Client.PutAsJsonAsync($"/guilds/{guild}/presence", new GuildPresenceRequest(true, "Majority guild"));
        var (admin, _) = await fixture.LoginAsync(guild * 1000 + 1, (guild, "G", true));
        var p1 = await fixture.LoginAsync(guild * 1000 + 2, (guild, "G", false));
        var p2 = await fixture.LoginAsync(guild * 1000 + 3, (guild, "G", false));
        var p3 = await fixture.LoginAsync(guild * 1000 + 4, (guild, "G", false));
        await admin.PostAsync($"/guilds/{guild}/game-nights/games/seed", null);
        var arcs = (await admin.GetFromJsonAsync<List<GameDto>>($"/guilds/{guild}/game-nights/games"))!.Single(g => g.Spec.Key == "arcs");
        var ev = await fixture.Client.PostAsJsonAsync($"/guilds/{guild}/events", new CreateEventRequest(9000, "Night", "in 3 hours", 65100));
        var eventId = (await ev.Content.ReadFromJsonAsync<EventDto>())!.Id;
        var table = (await (await admin.PostAsJsonAsync($"/guilds/{guild}/game-nights/events/{eventId}/tables", new CreateTableRequest(arcs.Id)))
            .Content.ReadFromJsonAsync<TableDto>())!;
        SubmittedPlayer P(long id, int place, string colour) => new(id, null, place, new Dictionary<string, string> { ["colour"] = colour, ["score"] = "5" });
        var report = new ResultSubmission([P(p1.Session.UserId, 1, "Red"), P(p2.Session.UserId, 2, "Blue"), P(p3.Session.UserId, 3, "White")]);

        var created = await p1.Client.PostAsJsonAsync($"/guilds/{guild}/game-nights/tables/{table.Id}/results", new SubmitResultRequest(report));
        var result = (await created.Content.ReadFromJsonAsync<ResultDto>())!;
        Assert.Equal(ResultStatus.Pending, result.Status); // one of three is not a majority

        var second = await p2.Client.PostAsJsonAsync($"/guilds/{guild}/game-nights/results/{result.Id}/confirm", new AnswerResultRequest());
        Assert.Equal(ResultStatus.Confirmed, (await second.Content.ReadFromJsonAsync<ResultDto>())!.Status);
    }
}
