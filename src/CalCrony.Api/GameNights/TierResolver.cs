using System.Security.Claims;
using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.GameNights;

/// <summary>What a tier lookup found for one caller in one guild.</summary>
/// <param name="Access">The caller's standing in the guild (None when not a member).</param>
/// <param name="Tier">The tier, or Member when it could not be confirmed.</param>
/// <param name="Verified">False when the role snapshot could not answer.</param>
public readonly record struct TierCheck(GuildAccess Access, GameNightsTier Tier, bool Verified);

/// <summary>Resolves a caller's Game Nights tier from the guild's bot-written role snapshot
/// (ADR 0004), per request. Rules, in order:
/// <list type="bullet">
/// <item>The bot is trusted (it checks Discord live before calling) and counts as Admin.</item>
/// <item>A web caller must be in the guild (membership snapshot) to have any tier.</item>
/// <item>A server manager (Manage Server or owner) is Admin, so a fresh server can be configured
/// before any role is mapped.</item>
/// <item>Otherwise the tier is the highest mapped role the caller holds per the snapshot. With no
/// mapping, everyone is a Member.</item>
/// <item>If the snapshot cannot answer (guild never synced, marker older than 30 minutes, or a mapped
/// role not yet checked) the tier is unverified: Member-level access still works, anything higher is
/// refused with 409 rather than guessed, as the signup restrictions do.</item>
/// </list></summary>
/// <param name="db">The database context.</param>
/// <param name="access">The guild-membership guard service.</param>
/// <param name="clock">The time source.</param>
public sealed class TierResolver(CalCronyDbContext db, GuildAccessService access, IClock clock)
{
    /// <summary>Resolves the caller's tier in a guild.</summary>
    /// <param name="user">The authenticated principal.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The access level, tier and whether it is verified.</returns>
    public async Task<TierCheck> ResolveAsync(ClaimsPrincipal user, long guildId, CancellationToken cancellationToken)
    {
        if (user.IsBot())
        {
            return new TierCheck(GuildAccess.Manager, GameNightsTier.Admin, true);
        }

        if (user.WebUserId() is not { } userId)
        {
            return new TierCheck(GuildAccess.None, GameNightsTier.Member, true);
        }

        var standing = await access.CheckAsync(userId, guildId, cancellationToken);
        if (standing is GuildAccess.None or GuildAccess.Stale)
        {
            return new TierCheck(standing, GameNightsTier.Member, true);
        }

        if (standing == GuildAccess.Manager)
        {
            return new TierCheck(standing, GameNightsTier.Admin, true);
        }

        var mapping = await db.GameNightsTierRoles
            .Where(t => t.GuildId == guildId)
            .Select(t => new TierRoleDto(t.RoleId, t.Tier))
            .ToListAsync(cancellationToken);
        if (mapping.Count == 0)
        {
            return new TierCheck(standing, GameNightsTier.Member, true);
        }

        var mappedIds = mapping.Select(m => m.RoleId).ToList();
        var snapshot = await db.Guilds
            .Where(g => g.Id == guildId)
            .Select(g => new
            {
                g.RolesSyncedAt,
                Roles = db.GuildRoles
                    .Where(r => r.GuildId == guildId && mappedIds.Contains(r.RoleId))
                    .Select(r => new { r.RoleId, r.Name })
                    .ToList(),
                Held = db.GuildMemberRoles
                    .Where(m => m.GuildId == guildId && m.UserId == userId)
                    .Select(m => m.RoleIds)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var fresh = RoleRestriction.IsSnapshotFresh(snapshot?.RolesSyncedAt, clock.GetCurrentInstant());
        var checkedRoles = snapshot?.Roles.ToDictionary(r => r.RoleId, r => r.Name) ?? [];

        // A checked-and-deleted role (null name) holds nobody; a mapped role with no checked row at
        // all has not been looked at since it was mapped, so absence there means "unknown".
        var live = mapping.Where(m => !(checkedRoles.TryGetValue(m.RoleId, out var name) && name is null)).ToList();
        if (!fresh || live.Any(m => !checkedRoles.ContainsKey(m.RoleId)))
        {
            return new TierCheck(standing, GameNightsTier.Member, false);
        }

        return new TierCheck(standing, TierLogic.Resolve(snapshot?.Held ?? [], live), true);
    }

    /// <summary>Checks a caller against a required tier.</summary>
    /// <param name="user">The authenticated principal.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="required">The lowest tier allowed.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Null when the caller may proceed; otherwise the 403 (not in the guild, or too low a
    /// tier) or 409 (tier unverifiable) response.</returns>
    public async Task<IResult?> DenyAsync(
        ClaimsPrincipal user, long guildId, GameNightsTier required, CancellationToken cancellationToken)
    {
        var check = await ResolveAsync(user, guildId, cancellationToken);
        switch (check.Access)
        {
            case GuildAccess.None:
                return GuildAccessService.Forbidden();
            case GuildAccess.Stale:
                return GuildAccessService.StaleSnapshot();
        }

        if (check.Tier >= required)
        {
            return null;
        }

        if (!check.Verified)
        {
            return Results.Conflict(new ErrorResponse(
                "We can't confirm your roles right now — try again in a few minutes, or do this from Discord.",
                GameNightsErrorCodes.TierUnverifiable));
        }

        return Results.Json(
            new ErrorResponse($"That needs the {required} role for Game Nights."),
            statusCode: StatusCodes.Status403Forbidden);
    }
}
