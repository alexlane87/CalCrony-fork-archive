using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Endpoints;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;

namespace CalCrony.Api.GameNights;

/// <summary>Step 1 routes: the caller's own tier, one probe per tier (so each tier's gate is
/// testable end to end), and the admin-only role-to-tier mapping. Every route is guild-scoped and
/// registered only when <c>GameNights:Enabled</c> is true.</summary>
public static class GameNightsEndpoints
{
    /// <summary>The most roles one guild may map.</summary>
    public const int MaxMappedRoles = 50;

    /// <summary>Maps the Game Nights routes.</summary>
    /// <param name="app">The route builder.</param>
    public static void MapGameNightsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/guilds/{guildId:long}/game-nights");

        g.MapGet("/me", GetMyTier);

        g.MapGet("/probe/member", () => Results.Ok(new { tier = GameNightsTier.Member })).RequireTier(GameNightsTier.Member);
        g.MapGet("/probe/organiser", () => Results.Ok(new { tier = GameNightsTier.Organiser })).RequireTier(GameNightsTier.Organiser);
        g.MapGet("/probe/moderator", () => Results.Ok(new { tier = GameNightsTier.Moderator })).RequireTier(GameNightsTier.Moderator);
        g.MapGet("/probe/admin", () => Results.Ok(new { tier = GameNightsTier.Admin })).RequireTier(GameNightsTier.Admin);

        g.MapVenueEndpoints();
        g.MapGameEndpoints();

        g.MapGet("/tier-roles", GetTierRoles).RequireTier(GameNightsTier.Admin);
        g.MapPut("/tier-roles", PutTierRoles).RequireTier(GameNightsTier.Admin);
    }

    private static async Task<IResult> GetMyTier(
        long guildId, HttpContext context, TierResolver tiers, CancellationToken cancellationToken)
    {
        var check = await tiers.ResolveAsync(context.User, guildId, cancellationToken);
        return check.Access switch
        {
            GuildAccess.None => GuildAccessService.Forbidden(),
            GuildAccess.Stale => GuildAccessService.StaleSnapshot(),
            _ => Results.Ok(new MyTierResponse(check.Tier, check.Verified)),
        };
    }

    private static async Task<IResult> GetTierRoles(long guildId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var roles = await db.GameNightsTierRoles
            .Where(t => t.GuildId == guildId)
            .OrderByDescending(t => t.Tier).ThenBy(t => t.RoleId)
            .Select(t => new TierRoleDto(t.RoleId, t.Tier))
            .ToListAsync(cancellationToken);
        return Results.Ok(new TierRolesResponse(roles));
    }

    // Replaces the guild's mapping. Roles that were not watched before are dropped from the
    // snapshot first (as for a new signup restriction), so tiers fail closed until the bot's next
    // sync has checked them, rather than answering from leftovers.
    private static async Task<IResult> PutTierRoles(
        long guildId, PutTierRolesRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (request.Roles is null)
        {
            return Results.BadRequest(new ErrorResponse("Roles is required (an empty list clears the mapping)."));
        }

        if (request.Roles.Count > MaxMappedRoles)
        {
            return Results.BadRequest(new ErrorResponse($"At most {MaxMappedRoles} roles can be mapped."));
        }

        if (request.Roles.Any(r => r.RoleId <= 0 || !Enum.IsDefined(r.Tier) || r.Tier == GameNightsTier.Member))
        {
            return Results.BadRequest(new ErrorResponse(
                "Each entry needs a role id and a tier of Organiser, Moderator or Admin (everyone is a Member already)."));
        }

        if (request.Roles.GroupBy(r => r.RoleId).Any(group => group.Count() > 1))
        {
            return Results.BadRequest(new ErrorResponse("A role can map to only one tier."));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RoleSnapshotEndpoints.LockGuildRowAsync(db, guildId, cancellationToken);
        await RoleSnapshotEndpoints.InvalidateNewlyWatchedAsync(
            db, guildId, request.Roles.Select(r => r.RoleId), cancellationToken);

        await db.GameNightsTierRoles.Where(t => t.GuildId == guildId).ExecuteDeleteAsync(cancellationToken);
        foreach (var role in request.Roles)
        {
            db.GameNightsTierRoles.Add(new GameNightsTierRole { GuildId = guildId, RoleId = role.RoleId, Tier = role.Tier });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(new TierRolesResponse([.. request.Roles.OrderByDescending(r => r.Tier).ThenBy(r => r.RoleId)]));
    }
}
