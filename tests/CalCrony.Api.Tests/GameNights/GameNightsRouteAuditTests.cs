using CalCrony.Api.GameNights;
using CalCrony.Contracts.GameNights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests.GameNights;

/// <summary>Step 11's endpoint review, made permanent. Every Game Nights route is pinned here to the tier it needs and to
/// whether only the bot may call it. A new route fails this test until someone writes down its tier, and loosening a route
/// fails it too, so a tier cannot drift without the change showing up in a diff. (What each route does with the visibility
/// flag is covered where the routes are: tables, results and signals filter by what the caller can see.)</summary>
public class GameNightsRouteAuditTests(GameNightsFixture fixture) : IClassFixture<GameNightsFixture>
{
    private const string Prefix = "/guilds/{guildId:long}/game-nights";

    private const GameNightsTier M = GameNightsTier.Member;
    private const GameNightsTier O = GameNightsTier.Organiser;
    private const GameNightsTier Mod = GameNightsTier.Moderator;
    private const GameNightsTier A = GameNightsTier.Admin;

    /// <summary>Method, route (after the Game Nights prefix), lowest tier.</summary>
    private static readonly (string Method, string Route, GameNightsTier? Tier)[] Expected =
    [
        // tier and setup
        ("GET", "/me", null), // answers for any signed-in caller: it is how a client learns its own tier
        ("GET", "/probe/member", M), ("GET", "/probe/organiser", O), ("GET", "/probe/moderator", Mod), ("GET", "/probe/admin", A),
        ("GET", "/tier-roles", A), ("PUT", "/tier-roles", A),

        // venues
        ("GET", "/venues", M), ("POST", "/venues", O), ("PATCH", "/venues/{venueId:guid}", O), ("DELETE", "/venues/{venueId:guid}", O),
        ("POST", "/venues/{venueId:guid}/areas", O), ("PATCH", "/venues/{venueId:guid}/areas/{areaId:guid}", O),
        ("DELETE", "/venues/{venueId:guid}/areas/{areaId:guid}", O),
        ("GET", "/events/{eventId:guid}/venue", M), ("PUT", "/events/{eventId:guid}/venue", O),
        ("PUT", "/events/{eventId:guid}/venue/areas/{areaId:guid}", O),

        // games and roles
        ("GET", "/games", M), ("GET", "/games/{gameId:guid}", M), ("POST", "/games", O), ("PUT", "/games/{gameId:guid}", O),
        ("POST", "/games/{gameId:guid}/retire", O), ("POST", "/games/{gameId:guid}/reinstate", O), ("POST", "/games/seed", A),
        ("POST", "/games/{gameId:guid}/role/create", O), ("PUT", "/games/{gameId:guid}/role-status", M),
        ("POST", "/games/{gameId:guid}/follow", M), ("GET", "/games/following", M), ("GET", "/game-types", M), ("PUT", "/game-types", A),

        // tables
        ("GET", "/events/{eventId:guid}/tables", M), ("POST", "/events/{eventId:guid}/tables", O),
        ("PUT", "/events/{eventId:guid}/table-defaults", O), ("GET", "/tables/{tableId:guid}", M), ("PATCH", "/tables/{tableId:guid}", O),
        ("POST", "/tables/{tableId:guid}/cancel", O), ("POST", "/tables/{tableId:guid}/seats", M),
        ("DELETE", "/tables/{tableId:guid}/seats/{userId:long}", M), ("PUT", "/tables/{tableId:guid}/thread", M),

        // what the night wants to play
        ("GET", "/events/{eventId:guid}/signals", M), ("GET", "/events/{eventId:guid}/signals/roster", M),
        ("GET", "/events/{eventId:guid}/signals/people", O), ("GET", "/events/{eventId:guid}/signals/mine", M),
        ("PUT", "/events/{eventId:guid}/signals/mine", M), ("PUT", "/events/{eventId:guid}/signals/{gameId:guid}", M),

        // results
        ("GET", "/results", M), ("GET", "/tables/{tableId:guid}/results", M), ("POST", "/tables/{tableId:guid}/results", M),
        ("GET", "/results/{resultId:guid}", M), ("POST", "/results/{resultId:guid}/confirm", M), ("POST", "/results/{resultId:guid}/reject", M),
        ("POST", "/results/{resultId:guid}/resolve", Mod), ("PUT", "/results/{resultId:guid}/summary-message", M),

        // leagues
        ("GET", "/games/{gameId:guid}/league", M), ("GET", "/games/{gameId:guid}/league/history", M), ("GET", "/leagues/me", M),
        ("POST", "/games/{gameId:guid}/league/seasons", O), ("POST", "/games/{gameId:guid}/league/seasons/{seasonId:guid}/end", O),
        ("POST", "/games/{gameId:guid}/league/recompute", Mod),

        // names and guests
        ("PUT", "/member-names", M), ("GET", "/member-names/wanted", M), ("GET", "/names", M),
        ("GET", "/guests", O), ("POST", "/guests", O), ("DELETE", "/guests/{guestId:guid}", Mod),
    ];

    /// <summary>The routes only the bot may call (it speaks for Discord: thread ids, names, role state, post ids).</summary>
    private static readonly string[] BotOnly =
    [
        "PUT /games/{gameId:guid}/role-status", "PUT /tables/{tableId:guid}/thread", "PUT /results/{resultId:guid}/summary-message",
        "PUT /member-names", "GET /member-names/wanted",
    ];

    private List<(string Key, string Method, string Route, GameNightsTier? Tier, bool BotOnly)> Actual()
    {
        var source = fixture.Factory.Services.GetRequiredService<EndpointDataSource>();
        return [.. source.Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith(Prefix, StringComparison.Ordinal))
            .SelectMany(e =>
            {
                var route = e.RoutePattern.RawText![Prefix.Length..];
                var tier = e.Metadata.GetMetadata<RequiredTier>()?.Tier;
                var bot = e.Metadata.OfType<IAuthorizeData>().Any(a => a.Policy == "BotOnly");
                return (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(m => ($"{m} {route}", m, route, tier, bot));
            })];
    }

    [Fact]
    public void Every_game_nights_route_is_pinned_to_its_tier_and_nothing_is_missing_or_unlisted()
    {
        var actual = Actual().ToDictionary(a => a.Key, a => a.Tier);
        var expected = Expected.ToDictionary(e => $"{e.Method} {e.Route}", e => e.Tier);

        Assert.Equal([], actual.Keys.Except(expected.Keys).Order()); // a new route needs a tier written down here
        Assert.Equal([], expected.Keys.Except(actual.Keys).Order()); // a pinned route that no longer exists
        foreach (var (key, tier) in expected)
        {
            Assert.True(actual[key] == tier, $"{key} needs {tier?.ToString() ?? "no tier"} but the route has {actual[key]?.ToString() ?? "no tier"}");
        }
    }

    [Fact]
    public void Only_the_tier_probe_route_is_open_to_every_signed_in_caller()
    {
        Assert.Equal(["GET /me"], Actual().Where(a => a.Tier is null).Select(a => a.Key));
    }

    [Fact]
    public void Exactly_the_routes_that_speak_for_discord_are_bot_only()
    {
        Assert.Equal([.. BotOnly.Order()], Actual().Where(a => a.BotOnly).Select(a => a.Key).Order());
    }
}
