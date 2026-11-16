using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using CalCrony.Contracts.GameNights;
using CalCrony.Web.GameNights;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests.GameNights;

/// <summary>The games page and the add-a-game wizard: what each tier sees, the role step's guidance and
/// warnings, the follow toggle, and that the wizard sends the same definition the shared rules build.</summary>
public class GameComponentTests : TestContext
{
    private const long Guild = 99;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, (HttpStatusCode Status, object? Body)> routes = [];
    private readonly List<(string Method, string Path, string Body)> requests = [];

    public GameComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped(_ => new GameNightsWebApiClient(
            new HttpClient(new RecordingHandler(routes, requests)) { BaseAddress = new Uri("http://localhost") }));
        Services.AddScoped(_ => new CalCrony.Web.Api.CalCronyWebApiClient(
            new HttpClient(new RecordingHandler(routes, requests)) { BaseAddress = new Uri("http://localhost") }));
    }

    private static GameDto Game(string name, long? role = null, GameRoleState state = GameRoleState.None, string? note = null, bool active = true) =>
        new(Guid.NewGuid(), active,
            new GameSpec(name.ToLowerInvariant(), name, GameResultType.RankedPlacement, 2, 4, 4, true, 32, 1000,
                [new("players", "Players", GameFieldKind.Player, GameFieldScope.Table, true),
                 new("score", "Final score", GameFieldKind.Number, GameFieldScope.Player, true)], role),
            state, note);

    private void Tier(GameNightsTier tier, bool verified = true) =>
        routes[$"GET /guilds/{Guild}/game-nights/me"] = (HttpStatusCode.OK, new MyTierResponse(tier, verified));

    private void Games(params GameDto[] games) =>
        routes[$"GET /guilds/{Guild}/game-nights/games"] = (HttpStatusCode.OK, games.ToList());

    private void Following(bool known, params Guid[] ids) =>
        routes[$"GET /guilds/{Guild}/game-nights/games/following"] = (HttpStatusCode.OK, new FollowingResponse(known, ids));

    private void Types(params GameTypeDto[] types) =>
        routes[$"GET /guilds/{Guild}/game-nights/game-types"] = (HttpStatusCode.OK, types.ToList());

    private IRenderedComponent<GuildGames> Page() => Render<GuildGames>(p => p.Add(c => c.GuildId, Guild));

    private static IReadOnlyList<AngleSharp.Dom.IElement> Buttons(IRenderedComponent<GuildGames> cut) => cut.FindAll("button");

    [Fact]
    public void The_tabs_are_hidden_when_game_nights_is_off()
    {
        var off = Render<GameNightsTabs>(p => p.Add(c => c.GuildId, Guild));

        off.WaitForState(() => true);
        Assert.Empty(off.Markup.Trim());
    }

    [Fact]
    public void The_tabs_show_venues_and_games_when_game_nights_is_on()
    {
        Tier(GameNightsTier.Member);
        var on = Render<GameNightsTabs>(p => p.Add(c => c.GuildId, Guild).Add(c => c.Active, "games"));
        on.WaitForAssertion(() => Assert.Contains("Games", on.Markup));
        Assert.Contains("Venues", on.Markup);
        Assert.Contains("/games", on.Find("a.btn-primary").GetAttribute("href"));
    }

    [Fact]
    public void A_member_sees_the_games_with_a_follow_button_and_no_editing_controls()
    {
        Tier(GameNightsTier.Member);
        var chess = Game("Chess", role: 5);
        Games(chess, Game("Go"));
        Following(known: true);
        Types();

        var cut = Page();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=game-card]").Count));
        Assert.Contains(Buttons(cut), b => b.TextContent.Trim() == "Follow");
        Assert.Single(Buttons(cut)); // only Chess has a role to follow; no editing buttons
        Assert.DoesNotContain("Add a game", cut.Markup);
    }

    [Fact]
    public void A_game_the_member_follows_shows_as_followed_and_an_unknown_snapshot_says_so()
    {
        Tier(GameNightsTier.Member);
        var chess = Game("Chess", role: 5);
        Games(chess);
        Following(known: true, chess.Id);
        Types();
        var followed = Page();
        followed.WaitForAssertion(() => Assert.Contains(Buttons(followed), b => b.TextContent.Contains("Following")));
        Assert.DoesNotContain("can't tell", followed.Markup);

        Following(known: false);
        var unknown = Page();
        unknown.WaitForAssertion(() => Assert.Contains("can't tell if you already follow", unknown.Markup));
    }

    [Fact]
    public void Pressing_follow_asks_the_api_and_flips_the_button()
    {
        Tier(GameNightsTier.Member);
        var chess = Game("Chess", role: 5);
        Games(chess);
        Following(known: true);
        Types();
        routes[$"POST /guilds/{Guild}/game-nights/games/{chess.Id}/follow"] = (HttpStatusCode.Accepted, new { });
        var cut = Page();
        cut.WaitForAssertion(() => Assert.Contains(Buttons(cut), b => b.TextContent.Trim() == "Follow"));

        Buttons(cut).Single(b => b.TextContent.Trim() == "Follow").Click();

        cut.WaitForAssertion(() => Assert.Contains(Buttons(cut), b => b.TextContent.Contains("Following")));
        var sent = Assert.Single(requests, r => r.Method == "POST" && r.Path.EndsWith("/follow"));
        Assert.Contains("\"follow\":true", sent.Body);
    }

    [Fact]
    public void A_pending_role_and_a_failed_role_show_what_is_happening_and_why()
    {
        Tier(GameNightsTier.Member);
        Games(
            Game("Chess", state: GameRoleState.Pending),
            Game("Go", state: GameRoleState.Failed, note: "I can't create roles here: my role needs the Manage Roles permission."));
        Following(true);
        Types();

        var cut = Page();

        cut.WaitForAssertion(() => Assert.Contains("creating this game's role", cut.Find("[data-testid=role-pending]").TextContent));
        Assert.Contains("Manage Roles permission", cut.Find("[data-testid=role-warning]").TextContent);
    }

    [Fact]
    public void An_organiser_can_add_and_retire_games_and_give_a_roleless_game_a_role()
    {
        Tier(GameNightsTier.Organiser);
        Games(Game("Chess"));
        Following(true);
        Types();

        var cut = Page();

        cut.WaitForAssertion(() => Assert.Contains("Add a game", cut.Markup));
        var labels = Buttons(cut).Select(b => b.TextContent.Trim()).ToList();
        Assert.Contains("Create a role", labels);
        Assert.Contains("Attach a role", labels);
        Assert.Contains("Retire", labels);
        Assert.DoesNotContain(labels, l => l.StartsWith("Add the standard games")); // admins only
    }

    [Fact]
    public void An_admin_also_sees_the_standard_games_button_and_manages_the_game_types()
    {
        Tier(GameNightsTier.Admin);
        Games();
        Following(true);
        Types(new GameTypeDto(77, "Board games"));

        var cut = Page();

        cut.WaitForAssertion(() => Assert.Contains("Add the standard games", cut.Markup));
        Assert.Contains("Board games", cut.Find("[data-testid=types]").TextContent);
        Assert.Contains(Buttons(cut), b => b.TextContent.Trim() == "Remove");
        Assert.Contains(Buttons(cut), b => b.TextContent.Trim() == "Mark as a type");
    }

    [Fact]
    public void The_wizard_walks_four_steps_warns_about_the_bots_needs_and_sends_the_shared_definition()
    {
        Tier(GameNightsTier.Organiser);
        Games();
        Following(true);
        Types();
        var created = Game("Root");
        routes[$"POST /guilds/{Guild}/game-nights/games"] = (HttpStatusCode.Created, created);
        routes[$"POST /guilds/{Guild}/game-nights/games/{created.Id}/role/create"] = (HttpStatusCode.Accepted, created with { RoleState = GameRoleState.Pending });
        var cut = Page();
        cut.WaitForAssertion(() => Assert.Contains("Add a game", cut.Markup));
        Buttons(cut).Single(b => b.TextContent.Contains("Add a game")).Click();

        // Step 1: the basics.
        Assert.Contains("Step 1 of 4", cut.Markup);
        cut.Find("#wizName").Input("Root");
        cut.Find("#wizMin").Change("2");
        cut.Find("#wizMax").Change("6");
        cut.Find("#wizUsual").Change("4");
        NextButton(cut).Click();

        // Step 2 and 3.
        Assert.Contains("Step 2 of 4", cut.Markup);
        cut.Find("#wizType").Change(nameof(GameResultType.RankedPlacement));
        NextButton(cut).Click();
        Assert.Contains("Step 3 of 4", cut.Markup);
        NextButton(cut).Click();

        // Step 4: the role, with the guidance the plan asks for.
        Assert.Contains("Step 4 of 4", cut.Markup);
        var guidance = cut.Find("[data-testid=role-guidance]").TextContent;
        Assert.Contains("Manage Roles", guidance);
        Assert.Contains("above", guidance);
        Assert.Contains("“Root”", cut.Markup); // default: create a role named for the game
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Add game").Click();

        cut.WaitForAssertion(() => Assert.Contains(requests, r => r.Path.EndsWith("/role/create")));
        var post = Assert.Single(requests, r => r.Method == "POST" && r.Path == $"/guilds/{Guild}/game-nights/games");
        var spec = JsonSerializer.Deserialize<GameSpec>(post.Body, Web)!;
        Assert.Equal(("root", "Root", GameResultType.RankedPlacement, 2, 6, 4), (spec.Key, spec.Name, spec.ResultType, spec.MinSize, spec.MaxSize, spec.DefaultSize));
        Assert.Equal(["players", "score"], spec.Fields.Select(f => f.Key));
        Assert.Null(spec.RoleId);
    }

    [Fact]
    public void Attaching_an_existing_role_sends_its_id_and_does_not_ask_the_bot_to_create_one()
    {
        Tier(GameNightsTier.Organiser);
        Games();
        Following(true);
        Types();
        var created = Game("Chess", role: 123456789012);
        routes[$"POST /guilds/{Guild}/game-nights/games"] = (HttpStatusCode.Created, created);
        var cut = Page();
        cut.WaitForAssertion(() => Assert.Contains("Add a game", cut.Markup));
        Buttons(cut).Single(b => b.TextContent.Contains("Add a game")).Click();
        cut.Find("#wizName").Input("Chess");
        NextButton(cut).Click();
        NextButton(cut).Click();
        NextButton(cut).Click();

        cut.Find("#roleAttach").Change(true);
        cut.Find("#wizRoleId").Input("123456789012");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Add game").Click();

        cut.WaitForAssertion(() => Assert.Contains(requests, r => r.Method == "POST" && r.Path.EndsWith("/games")));
        var post = Assert.Single(requests, r => r.Method == "POST" && r.Path.EndsWith("/games"));
        Assert.Equal(123456789012L, JsonSerializer.Deserialize<GameSpec>(post.Body, Web)!.RoleId);
        Assert.DoesNotContain(requests, r => r.Path.EndsWith("/role/create"));
    }

    [Fact]
    public void A_bad_role_id_or_a_bad_name_is_caught_before_anything_is_sent()
    {
        Tier(GameNightsTier.Organiser);
        Games();
        Following(true);
        Types();
        var cut = Page();
        cut.WaitForAssertion(() => Assert.Contains("Add a game", cut.Markup));
        Buttons(cut).Single(b => b.TextContent.Contains("Add a game")).Click();

        cut.Find("#wizName").Input("   ");
        NextButton(cut).Click();
        Assert.Contains("Step 1 of 4", cut.Markup);
        Assert.Contains("game name is required", cut.Find(".alert-danger").TextContent);

        cut.Find("#wizName").Input("Chess");
        NextButton(cut).Click();
        NextButton(cut).Click();
        NextButton(cut).Click();
        cut.Find("#roleAttach").Change(true);
        cut.Find("#wizRoleId").Input("not a number");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Add game").Click();

        Assert.Contains("Paste the role's ID", cut.Find(".alert-danger").TextContent);
        Assert.DoesNotContain(requests, r => r.Method == "POST");
    }

    private static AngleSharp.Dom.IElement NextButton(IRenderedComponent<GuildGames> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Next");

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
