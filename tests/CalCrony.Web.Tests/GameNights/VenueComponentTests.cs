using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using CalCrony.Contracts.GameNights;
using CalCrony.Web.Api;
using CalCrony.Web.GameNights;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests.GameNights;

/// <summary>The venues page, the guild tab and the event page's venue panel: what each tier sees,
/// and that nothing renders when Game Nights is not switched on.</summary>
public class VenueComponentTests : TestContext
{
    private const long Guild = 77;
    private static readonly Guid EventId = Guid.NewGuid();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly VenueAreaDto Upstairs = new(Guid.NewGuid(), "Upstairs", 5, true);
    private static readonly VenueDto Hall = new(Guid.NewGuid(), "The Hall", "1 High Street", 10, true, [Upstairs]);

    private readonly Dictionary<string, (HttpStatusCode Status, object? Body)> routes = [];

    public VenueComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped(_ => new GameNightsWebApiClient(
            new HttpClient(new RouteHandler(routes)) { BaseAddress = new Uri("http://localhost") }));
        // GuildHeading looks the server's name up; an unknown route answers 404 and it renders nothing.
        Services.AddScoped(_ => new CalCronyWebApiClient(
            new HttpClient(new RouteHandler(routes)) { BaseAddress = new Uri("http://localhost") }));
    }

    private void Tier(GameNightsTier tier, bool verified = true) =>
        routes[$"/guilds/{Guild}/game-nights/me"] = (HttpStatusCode.OK, new MyTierResponse(tier, verified));

    private void Venues(params VenueDto[] venues) =>
        routes[$"/guilds/{Guild}/game-nights/venues"] = (HttpStatusCode.OK, venues.ToList());

    private void EventVenue(EventVenueDto dto) =>
        routes[$"/guilds/{Guild}/game-nights/events/{EventId}/venue"] = (HttpStatusCode.OK, dto);

    [Fact]
    public void The_venues_tab_is_hidden_when_game_nights_is_off()
    {
        var cut = Render<GameNightsTabs>(p => p.Add(c => c.GuildId, Guild));

        cut.WaitForState(() => true);
        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void The_venues_tab_shows_and_highlights_when_game_nights_is_on()
    {
        Tier(GameNightsTier.Member);

        var cut = Render<GameNightsTabs>(p => p.Add(c => c.GuildId, Guild).Add(c => c.Active, "venues"));

        cut.WaitForAssertion(() => Assert.Contains("Venues", cut.Markup));
        Assert.Contains("btn-primary", cut.Markup);
    }

    [Fact]
    public void A_member_sees_the_venues_but_no_editing_controls()
    {
        Tier(GameNightsTier.Member);
        Venues(Hall);

        var cut = Render<GuildVenues>(p => p.Add(c => c.GuildId, Guild));

        cut.WaitForAssertion(() => Assert.Contains("The Hall", cut.Markup));
        Assert.Contains("1 High Street", cut.Markup);
        Assert.Contains("Upstairs", cut.Markup);
        Assert.Contains("10 places", cut.Markup);
        Assert.DoesNotContain("Add a venue", cut.Markup);
        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public void An_organiser_sees_the_add_form_and_per_venue_controls()
    {
        Tier(GameNightsTier.Organiser);
        Venues(Hall);

        var cut = Render<GuildVenues>(p => p.Add(c => c.GuildId, Guild));

        cut.WaitForAssertion(() => Assert.Contains("Add a venue", cut.Markup));
        Assert.Contains("＋ Area", cut.Markup);
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Retire");
    }

    [Fact]
    public void An_unverifiable_tier_pauses_editing_and_says_why()
    {
        Tier(GameNightsTier.Member, verified: false);
        Venues(Hall);

        var cut = Render<GuildVenues>(p => p.Add(c => c.GuildId, Guild));

        cut.WaitForAssertion(() => Assert.Contains("can't confirm your server roles", cut.Markup));
        Assert.DoesNotContain("Add a venue", cut.Markup);
    }

    [Fact]
    public void The_page_says_so_when_game_nights_is_off()
    {
        var cut = Render<GuildVenues>(p => p.Add(c => c.GuildId, Guild));

        cut.WaitForAssertion(() => Assert.Contains("isn't switched on", cut.Markup));
    }

    [Fact]
    public void The_event_panel_shows_the_limit_and_where_it_came_from_to_a_member_without_controls()
    {
        Tier(GameNightsTier.Member);
        EventVenue(new EventVenueDto(EventId, Hall, null, [Upstairs.Id],
            new EffectiveLimitDto(15, LimitSource.Venue, "The Hall 10 + Upstairs 5 = 15")));

        var cut = Render<EventVenuePanel>(p => p.Add(c => c.GuildId, Guild).Add(c => c.EventId, EventId));

        cut.WaitForAssertion(() => Assert.Contains("The Hall", cut.Markup));
        Assert.Contains("Attendee limit: 15", cut.Find("[data-testid=limit]").TextContent);
        Assert.Contains("The Hall 10 + Upstairs 5 = 15", cut.Markup);
        Assert.DoesNotContain("Save venue", cut.Markup);
    }

    [Fact]
    public void An_organiser_gets_the_venue_picker_override_and_area_toggles()
    {
        Tier(GameNightsTier.Admin);
        Venues(Hall);
        EventVenue(new EventVenueDto(EventId, Hall, 20, [],
            new EffectiveLimitDto(20, LimitSource.Override, "Set for this event: 20")));

        var cut = Render<EventVenuePanel>(p => p.Add(c => c.GuildId, Guild).Add(c => c.EventId, EventId));

        cut.WaitForAssertion(() => Assert.Contains("Save venue", cut.Markup));
        Assert.Equal(Hall.Id.ToString(), cut.Find("#venuePick").GetAttribute("value"));
        Assert.Equal("20", cut.Find("#limitOverride").GetAttribute("value"));
        Assert.Contains("Upstairs", cut.Markup);
        Assert.Contains("Set for this event: 20", cut.Markup);
    }

    [Fact]
    public void The_event_panel_renders_nothing_for_a_member_when_there_is_no_venue()
    {
        Tier(GameNightsTier.Member);
        EventVenue(new EventVenueDto(EventId, null, null, [], new EffectiveLimitDto(null, LimitSource.None, null)));

        var cut = Render<EventVenuePanel>(p => p.Add(c => c.GuildId, Guild).Add(c => c.EventId, EventId));

        cut.WaitForState(() => true);
        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void The_event_panel_renders_nothing_when_game_nights_is_off()
    {
        var cut = Render<EventVenuePanel>(p => p.Add(c => c.GuildId, Guild).Add(c => c.EventId, EventId));

        cut.WaitForState(() => true);
        Assert.Empty(cut.Markup.Trim());
    }

    private sealed class RouteHandler(Dictionary<string, (HttpStatusCode Status, object? Body)> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!routes.TryGetValue(path, out var hit))
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
