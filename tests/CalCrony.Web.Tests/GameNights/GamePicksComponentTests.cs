using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using CalCrony.Contracts.GameNights;
using CalCrony.Web.GameNights;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests.GameNights;

/// <summary>The "games tonight" section of the event page: demand with the no-teacher games highlighted, the
/// roster, the member's own picks (saved in one request) and the organiser-only list of everyone's answers.</summary>
public class GamePicksComponentTests : TestContext
{
    private const long Guild = 111;
    private const long Me = 500;
    private static readonly Guid EventId = Guid.NewGuid();
    private static readonly Guid Root = Guid.NewGuid();
    private static readonly Guid Arcs = Guid.NewGuid();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, (HttpStatusCode Status, object? Body)> routes = [];
    private readonly List<(string Method, string Path, string Body)> requests = [];

    public GamePicksComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped(_ => new GameNightsWebApiClient(
            new HttpClient(new RecordingHandler(routes, requests)) { BaseAddress = new Uri("http://localhost") }));
    }

    private string Night(string tail = "") => $"/guilds/{Guild}/game-nights/events/{EventId}/signals{tail}";

    private void Arrange(GameNightsTier tier, NightSignalsSummary summary, IEnumerable<TeacherRosterEntryDto>? roster = null,
        IEnumerable<SignalDto>? mine = null, IEnumerable<PersonSignalDto>? people = null)
    {
        routes[$"GET /guilds/{Guild}/game-nights/me"] = (HttpStatusCode.OK, new MyTierResponse(tier, true));
        routes[$"GET {Night()}"] = (HttpStatusCode.OK, summary);
        routes[$"GET {Night("/roster")}"] = (HttpStatusCode.OK, (roster ?? []).ToList());
        routes[$"GET {Night("/mine")}"] = (HttpStatusCode.OK, new MySignalsDto([.. mine ?? []]));
        routes[$"GET {Night("/people")}"] = (HttpStatusCode.OK, (people ?? []).ToList());
        routes[$"GET /guilds/{Guild}/game-nights/names"] = (HttpStatusCode.OK, new Dictionary<string, string> { ["77"] = "Sam T" });
    }

    private static NightSignalsSummary Summary() => new(
        [new GameDemandDto(Root, "Root", 1, 2, 0, 0), new GameDemandDto(Arcs, "Arcs", 0, 1, 2, 0)],
        Teachers: 1, Experienced: 3, Fresh: 2, Respondents: 5);

    private IRenderedComponent<EventGamesPanel> Panel() => Render<EventGamesPanel>(p => p
        .Add(c => c.GuildId, Guild).Add(c => c.EventId, EventId).Add(c => c.UserId, Me).Add(c => c.RefreshSeconds, 0));

    [Fact]
    public void Nothing_renders_when_game_nights_is_off_or_the_server_has_no_games()
    {
        var off = Panel();
        off.WaitForState(() => true);
        Assert.Empty(off.Markup.Trim());

        Arrange(GameNightsTier.Member, new NightSignalsSummary([], 0, 0, 0, 0));
        var none = Panel();
        none.WaitForState(() => true);
        Assert.Empty(none.Markup.Trim());
    }

    [Fact]
    public void The_demand_panel_shows_the_totals_and_highlights_a_game_nobody_can_teach()
    {
        Arrange(GameNightsTier.Member, Summary());

        var cut = Panel();

        cut.WaitForAssertion(() => Assert.Contains("5 answered", cut.Find("[data-testid=totals]").TextContent));
        Assert.Contains("1 teaching", cut.Find("[data-testid=totals]").TextContent);
        var flagged = cut.Find("[data-testid=needs-teacher]");
        Assert.Contains("Arcs", flagged.TextContent);
        Assert.Contains("needs a teacher", flagged.TextContent);
        Assert.Contains("table-warning", flagged.GetAttribute("class"));
        Assert.DoesNotContain("Root needs", cut.Markup);
        Assert.Contains("Root", cut.Find("[data-testid=game-row]").TextContent);
    }

    [Fact]
    public void The_roster_names_teachers_and_marks_the_viewer_as_you()
    {
        Arrange(GameNightsTier.Member, Summary(), roster: [new TeacherRosterEntryDto(Root, "Root", [Me, 77])]);

        var cut = Panel();

        cut.WaitForAssertion(() => Assert.Contains("Root", cut.Find("[data-testid=roster]").TextContent));
        Assert.Contains("you, Sam T", cut.Find("[data-testid=roster]").TextContent);
    }

    [Fact]
    public void The_members_own_answers_are_preselected_and_saving_sends_them_all_in_one_request()
    {
        Arrange(GameNightsTier.Member, Summary(), mine: [new SignalDto(Root, SignalKind.CanPlay)]);
        routes[$"PUT {Night("/mine")}"] = (HttpStatusCode.OK, new MySignalsDto([new SignalDto(Root, SignalKind.CanPlay), new SignalDto(Arcs, SignalKind.NewToIt)]));
        var cut = Panel();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=picks] select").Count));
        Assert.Equal("CanPlay", cut.Find("[data-testid=picks] select[aria-label=Root]").GetAttribute("value"));

        cut.Find("[data-testid=picks] select[aria-label=Arcs]").Change("NewToIt");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save my picks").Click();

        cut.WaitForAssertion(() => Assert.Contains("Saved", cut.Markup));
        var put = Assert.Single(requests, r => r.Method == "PUT");
        var sent = JsonSerializer.Deserialize<SetMySignalsRequest>(put.Body, Web)!;
        Assert.Equal(
            [(Root, SignalKind.CanPlay), (Arcs, SignalKind.NewToIt)],
            sent.Signals.OrderBy(s => s.GameId == Root ? 0 : 1).Select(s => (s.GameId, s.Kind)));
    }

    [Fact]
    public void Choosing_no_answer_leaves_the_game_out_of_what_is_sent()
    {
        Arrange(GameNightsTier.Member, Summary(), mine: [new SignalDto(Root, SignalKind.CanPlay)]);
        routes[$"PUT {Night("/mine")}"] = (HttpStatusCode.OK, new MySignalsDto([]));
        var cut = Panel();
        cut.WaitForAssertion(() => Assert.Equal("CanPlay", cut.Find("[data-testid=picks] select[aria-label=Root]").GetAttribute("value")));

        cut.Find("[data-testid=picks] select[aria-label=Root]").Change("");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save my picks").Click();

        cut.WaitForAssertion(() => Assert.Contains(requests, r => r.Method == "PUT"));
        Assert.Empty(JsonSerializer.Deserialize<SetMySignalsRequest>(requests.Single(r => r.Method == "PUT").Body, Web)!.Signals);
    }

    [Fact]
    public void Only_organisers_see_everyones_answers()
    {
        var people = new[] { new PersonSignalDto(77, Arcs, SignalKind.NewToIt), new PersonSignalDto(Me, Root, SignalKind.CanTeach) };
        Arrange(GameNightsTier.Member, Summary(), people: people);
        var member = Panel();
        member.WaitForAssertion(() => Assert.Contains("Games tonight", member.Markup));
        Assert.Empty(member.FindAll("[data-testid=people]"));
        Assert.DoesNotContain(requests, r => r.Path.EndsWith("/people")); // not even asked for

        var organiserCtx = new GamePicksComponentTests();
        organiserCtx.Arrange(GameNightsTier.Organiser, Summary(), people: people);
        var organiser = organiserCtx.Panel();

        organiser.WaitForAssertion(() => Assert.Contains("Everyone's answers", organiser.Markup));
        var text = organiser.Find("[data-testid=people]").TextContent;
        Assert.Contains("Sam T", text);
        Assert.DoesNotContain("user 77", text);
        Assert.Contains("Arcs: new to it", text);
        Assert.Contains("you", text);
        Assert.Contains("Root: can teach", text);
    }

    [Fact]
    public void A_refused_save_shows_the_reason_and_does_not_claim_it_saved()
    {
        Arrange(GameNightsTier.Member, Summary());
        routes[$"PUT {Night("/mine")}"] = (HttpStatusCode.Conflict, new { error = "That game night is over, so its answers are closed." });
        var cut = Panel();
        cut.WaitForAssertion(() => Assert.Contains("Save my picks", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save my picks").Click();

        cut.WaitForAssertion(() => Assert.Contains("answers are closed", cut.Find(".alert-danger").TextContent));
        Assert.DoesNotContain("Saved", cut.Markup);
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
                Content = new StringContent(JsonSerializer.Serialize(hit.Body, Web), Encoding.UTF8, "application/json"),
            };
        }
    }
}
