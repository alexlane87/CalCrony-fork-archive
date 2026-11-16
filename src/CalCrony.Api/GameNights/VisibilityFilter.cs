using CalCrony.Api.Data;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;

namespace CalCrony.Api.GameNights;

/// <summary>The shared visibility filter: queries over Game Nights rows go through here so a caller
/// who may not see a row simply does not find it (404, never 403). Mirrors
/// <see cref="VisibilityRules"/>; the rule is written twice because EF needs an expression.</summary>
public static class VisibilityFilter
{
    /// <summary>Restricts event extension rows to those the caller may see. Events with no extension
    /// row are public and are not covered here (callers look the extension up by event id).</summary>
    /// <param name="rows">The rows to filter.</param>
    /// <param name="db">The database context (for the invite list).</param>
    /// <param name="userId">The caller's Discord id.</param>
    /// <param name="tier">The caller's tier.</param>
    /// <returns>The visible rows.</returns>
    public static IQueryable<GameNightEventExtension> VisibleTo(
        this IQueryable<GameNightEventExtension> rows, CalCronyDbContext db, long userId, GameNightsTier tier)
    {
        var seesRestricted = tier >= GameNightsTier.Organiser;
        var seesInviteOnly = tier >= GameNightsTier.Moderator;
        return rows.Where(x =>
            x.Visibility == GameNightsVisibility.Public
            || (seesRestricted && x.Visibility == GameNightsVisibility.Restricted)
            || (seesInviteOnly && x.Visibility == GameNightsVisibility.InviteOnly)
            || db.GameNightInvites.Any(i =>
                i.Scope == GameNightInviteScope.Event && i.ScopeId == x.EventId && i.UserId == userId));
    }

    /// <summary>Whether a caller may see one event. An event with no extension row is public.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="eventId">The event id.</param>
    /// <param name="userId">The caller's Discord id.</param>
    /// <param name="tier">The caller's tier.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>True when the event is visible; a caller turns false into a 404.</returns>
    public static async Task<bool> CanSeeEventAsync(
        CalCronyDbContext db, Guid eventId, long userId, GameNightsTier tier, CancellationToken cancellationToken)
    {
        var extension = await db.GameNightEventExtensions
            .Where(x => x.EventId == eventId)
            .Select(x => x.Visibility)
            .Cast<GameNightsVisibility?>()
            .FirstOrDefaultAsync(cancellationToken);
        if (extension is null or GameNightsVisibility.Public)
        {
            return true;
        }

        return await db.GameNightEventExtensions
            .Where(x => x.EventId == eventId)
            .VisibleTo(db, userId, tier)
            .AnyAsync(cancellationToken);
    }
}
