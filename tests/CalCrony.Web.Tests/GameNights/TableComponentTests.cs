using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using CalCrony.Contracts.GameNights;
using CalCrony.Web.GameNights;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests.GameNights;

/// <summary>The table card and the event page's tables section: what a member and an organiser see, and
/// that nothing renders when Game Nights is off.</summary>
public class TableComponentTests : TestContext
{
    private const long Guild = 88;
    private const long Me = 500;
    private static readonly Guid EventId = Guid.NewGuid();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, (HttpStatusCode Status, object? Body)> routes = [];

    public TableComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped(_ => new GameNightsWebApiClient(
            new HttpClient(new RouteHandler(routes)) { BaseAddress = new Uri("http://localhost") }));
    }

    private static TableDto Table(int seated = 2, int size = 4, long? host = null, bool includeMe = false, TableStatus status = TableStatus.Open, long? thread = null)
    {
        var ids = Enumerable.Range(0, seated).Select(i => (long)(100 + i)).ToList();
        if (includeMe)
        {
            ids[0] = Me;
        }

        return new TableDto(
            Guid.NewGuid(), EventId, Guid.NewGuid(), "Root", 1, size, host, status, thread, GameNightsVisibility.Public,
            [.. ids.Select((id, i) => new TableSeatDto(id, i))]);
    }

    private void Tier(GameNightsTier tier) =>
        routes[$"/guilds/{Guild}/game-nights/me"] = (HttpStatusCode.OK, new MyTierResponse(tier, true));

    private void Tables(int? defaultSize, params TableDto[] tables) =>
        routes[$"/guilds/{Guild}/game-nights/events/{EventId}/tables"] = (HttpStatusCode.OK, new EventTablesDto(defaultSize, tables));

    private IRenderedComponent<TableCard> Card(TableDto table, bool organise = false) =>
        Render<TableCard>(p => p.Add(c => c.Table, table).Add(c => c.GuildId, Guild).Add(c => c.UserId, Me).Add(c => c.CanOrganise, organise));

    [Fact]
    public void A_card_shows_the_game_the_seats_the_host_and_who_you_are()
    {
        var cut = Card(Table(seated: 3, host: 101, includeMe: true));

        Assert.Contains("Root, table 1", cut.Markup);
        Assert.Equal("3 / 4 seats", cut.Find("[data-testid=seats]").TextContent.Trim());
        Assert.Contains("host", cut.Markup);
        Assert.Contains("you", cut.Markup);
    }

    [Fact]
    public void A_member_who_is_not_seated_can_sit_down_and_a_seated_one_can_leave()
    {
        var notSeated = Card(Table());
        Assert.Contains(notSeated.FindAll("button"), b => b.TextContent.Trim() == "Sit down");
        Assert.DoesNotContain(notSeated.FindAll("button"), b => b.TextContent.Trim() == "Leave table");

        var seated = Card(Table(includeMe: true));
        Assert.Contains(seated.FindAll("button"), b => b.TextContent.Trim() == "Leave table");
        Assert.DoesNotContain(seated.FindAll("button"), b => b.TextContent.Trim() == "Sit down");
    }

    [Fact]
    public void A_full_table_cannot_be_sat_at()
    {
        var cut = Card(Table(seated: 4, size: 4));

        var button = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Table full");
        Assert.True(button.HasAttribute("disabled"));
    }

    [Fact]
    public void Only_organisers_get_the_resize_host_and_cancel_controls()
    {
        var member = Card(Table(seated: 2, host: 100), organise: false);
        Assert.Empty(member.FindAll("select"));
        Assert.DoesNotContain(member.FindAll("button"), b => b.TextContent.Trim() == "Cancel table");

        var organiser = Card(Table(seated: 2, host: 100), organise: true);
        Assert.Contains(organiser.FindAll("button"), b => b.TextContent.Trim() == "Cancel table");
        Assert.Equal("100", organiser.Find("select").GetAttribute("value"));
    }

    [Fact]
    public void A_cancelled_table_offers_no_buttons_and_the_thread_link_points_at_discord()
    {
        var cancelled = Card(Table(status: TableStatus.Cancelled), organise: true);
        Assert.Contains("Cancelled", cancelled.Markup);
        Assert.Empty(cancelled.FindAll("button"));

        var threaded = Card(Table(thread: 4242));
        Assert.Contains($"https://discord.com/channels/{Guild}/4242", threaded.Find("a[target=_blank]").GetAttribute("href"));
    }

    [Fact]
    public void The_tables_section_lists_each_table_and_gives_members_no_form()
    {
        Tier(GameNightsTier.Member);
        Tables(null, Table(), Table(seated: 1));

        var cut = Render<EventTablesPanel>(p => p.Add(c => c.GuildId, Guild).Add(c => c.EventId, EventId).Add(c => c.UserId, Me));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=table-card]").Count));
        Assert.DoesNotContain("Open a table", cut.Markup);
    }

    [Fact]
    public void An_organiser_sees_the_open_a_table_form_even_with_no_tables()
    {
        Tier(GameNightsTier.Organiser);
        Tables(3);
        routes[$"/guilds/{Guild}/game-nights/games"] = (HttpStatusCode.OK, new List<GameDto>
        {
            new(Guid.NewGuid(), true, new GameSpec("root", "Root", GameResultType.RankedPlacement, 2, 6, 4, true, 32, 1000, [])),
        });

        var cut = Render<EventTablesPanel>(p => p.Add(c => c.GuildId, Guild).Add(c => c.EventId, EventId).Add(c => c.UserId, Me));

        cut.WaitForAssertion(() => Assert.Contains("Open a table", cut.Markup));
        Assert.Contains("Root (2–6 players)", cut.Markup);
        Assert.Equal("3", cut.Find("#nightSize").GetAttribute("value"));
        Assert.Contains("No tables yet", cut.Markup);
    }

    [Fact]
    public void A_member_sees_nothing_when_the_night_has_no_tables()
    {
        Tier(GameNightsTier.Member);
        Tables(null);

        var cut = Render<EventTablesPanel>(p => p.Add(c => c.GuildId, Guild).Add(c => c.EventId, EventId).Add(c => c.UserId, Me));

        cut.WaitForState(() => true);
        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void The_tables_section_renders_nothing_when_game_nights_is_off()
    {
        var cut = Render<EventTablesPanel>(p => p.Add(c => c.GuildId, Guild).Add(c => c.EventId, EventId).Add(c => c.UserId, Me));

        cut.WaitForState(() => true);
        Assert.Empty(cut.Markup.Trim());
    }

    private sealed class RouteHandler(Dictionary<string, (HttpStatusCode Status, object? Body)> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!routes.TryGetValue(request.RequestUri!.AbsolutePath, out var hit))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{\"error\":\"not found\"}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(hit.Status)
            {
                Content = new StringContent(JsonSerializer.Serialize(hit.Body, Web), Encoding.UTF8, "application/json"),
            });
        }
    }
}
