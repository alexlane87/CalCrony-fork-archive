using CalCrony.Contracts.GameNights;

namespace CalCrony.Api.GameNights;

/// <summary>Marks a route as guarded by <see cref="TierGuard.RequireTier"/>. The filter itself is invisible to anything that
/// inspects the route table, so this records the tier on the endpoint: the route audit test reads it to pin every Game Nights
/// route to its tier, and a route added without one fails that test.</summary>
/// <param name="Tier">The lowest tier allowed.</param>
public sealed record RequiredTier(GameNightsTier Tier);

/// <summary>Endpoint filter that enforces a minimum Game Nights tier on a route with a
/// <c>{guildId:long}</c> segment. Implemented as a filter rather than an ASP.NET policy because the
/// decision needs the route's guild and has two refusal shapes (403 and the 409 for an unverifiable
/// snapshot), which a policy cannot express.</summary>
public static class TierGuard
{
    /// <summary>Requires the caller to hold at least <paramref name="tier"/> in the route's guild.</summary>
    /// <param name="builder">The route.</param>
    /// <param name="tier">The lowest tier allowed.</param>
    /// <returns>The route, for chaining.</returns>
    public static RouteHandlerBuilder RequireTier(this RouteHandlerBuilder builder, GameNightsTier tier) =>
        builder.WithMetadata(new RequiredTier(tier)).AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (http.Request.RouteValues["guildId"] is not string raw || !long.TryParse(raw, out var guildId))
            {
                return Results.NotFound();
            }

            var resolver = http.RequestServices.GetRequiredService<TierResolver>();
            var denial = await resolver.DenyAsync(http.User, guildId, tier, http.RequestAborted);
            return denial ?? await next(context);
        });
}
