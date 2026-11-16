using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CalCrony.Api.Services;

/// <summary>Which Discord roles the API needs snapshots for: every role named by a LIVE signup
/// restriction — an option on a scheduled/started event, a running series' option template, or
/// an open poll — and every attendee role a live event or running series grants (#167: the web
/// names a role only from these snapshots, so a granted role without one printed as a raw id).
/// The set is derived on demand rather than stored, so it can never drift from the events,
/// series and polls that name or grant the roles; the bot reads it at Ready (and after any
/// command that names a role) and pushes exactly those roles' snapshots back. Retention uses the
/// same answer to drop the snapshots of guilds with no watched role left (ADR 0004).</summary>
public static class RoleWatchList
{
    /// <summary>The watched role ids per guild, for every guild with at least one watched role
    /// (bot-absent guilds included — callers filter). Guilds with none are absent.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Guild id to the distinct roles its live restrictions name and its live events and
    /// running series grant.</returns>
    public static Task<Dictionary<long, HashSet<long>>> WatchedByGuildAsync(
        CalCronyDbContext db, CancellationToken cancellationToken) =>
        WatchedAsync(db, guildId: null, cancellationToken);

    /// <summary>One guild's watched role ids — the per-guild form the bot syncs against, so a
    /// sync scans that guild's events, series and polls rather than every guild's.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The distinct roles the guild's live restrictions name and its live events and
    /// running series grant; empty when none.</returns>
    public static async Task<HashSet<long>> WatchedForGuildAsync(
        CalCronyDbContext db, long guildId, CancellationToken cancellationToken) =>
        (await WatchedAsync(db, guildId, cancellationToken)).GetValueOrDefault(guildId) ?? [];

    private static async Task<Dictionary<long, HashSet<long>>> WatchedAsync(
        CalCronyDbContext db, long? guildId, CancellationToken cancellationToken)
    {
        var watched = new Dictionary<long, HashSet<long>>();

        // Cardinality on the array column keeps the scan to options that name a role at all.
        var fromEvents = await db.RsvpOptions
            .Where(o => o.AllowedRoleIds.Length > 0 || o.AttendeeRoleId != null)
            .Join(
                db.Events.Where(e => (e.Status == EventStatus.Scheduled || e.Status == EventStatus.Started)
                                     && (guildId == null || e.GuildId == guildId)),
                o => o.EventId, e => e.Id,
                (o, e) => new { e.GuildId, o.AllowedRoleIds, o.AttendeeRoleId })
            .ToListAsync(cancellationToken);
        foreach (var option in fromEvents)
        {
            Add(watched, option.GuildId, NamedBy(option.AllowedRoleIds, option.AttendeeRoleId));
        }

        // A running series spawns its next occurrence from the template, so its restrictions and
        // attendee roles are live even between occurrences. Templates are JSON; the running set
        // is small, so the specs are read in memory rather than through a JSON query.
        var fromSeries = await db.EventSeries
            .Where(s => !s.Ended && s.RsvpOptionsJson != null && (guildId == null || s.GuildId == guildId))
            .Select(s => new { s.GuildId, s.RsvpOptionsJson })
            .ToListAsync(cancellationToken);
        foreach (var series in fromSeries)
        {
            foreach (var option in RsvpPolicy.OptionsFromTemplate(series.RsvpOptionsJson))
            {
                Add(watched, series.GuildId, NamedBy(option));
            }
        }

        var fromPolls = await db.Polls
            .Where(p => p.Status == PollStatus.Open && p.AllowedRoleIds.Length > 0 && (guildId == null || p.GuildId == guildId))
            .Select(p => new { p.GuildId, p.AllowedRoleIds })
            .ToListAsync(cancellationToken);
        foreach (var poll in fromPolls)
        {
            Add(watched, poll.GuildId, poll.AllowedRoleIds);
        }

        // Fork (Game Nights): roles mapped to a tier are watched permanently, or their holders
        // would never be snapshotted and retention would drop the guild's snapshot.
        var fromTiers = await db.GameNightsTierRoles
            .Where(t => guildId == null || t.GuildId == guildId)
            .Select(t => new { t.GuildId, t.RoleId })
            .ToListAsync(cancellationToken);
        foreach (var tier in fromTiers)
        {
            Add(watched, tier.GuildId, [tier.RoleId]);
        }

        // Fork (Game Nights): a game's follow role (so the web can show who follows what) and the roles an
        // admin marked as game types (so interest by type can be read) are watched too.
        var fromGames = await db.GameNightGames
            .Where(g => g.Active && g.RoleId != null && (guildId == null || g.GuildId == guildId))
            .Select(g => new { g.GuildId, RoleId = g.RoleId!.Value })
            .ToListAsync(cancellationToken);
        foreach (var game in fromGames)
        {
            Add(watched, game.GuildId, [game.RoleId]);
        }

        var fromTypes = await db.GameNightGameTypes
            .Where(t => guildId == null || t.GuildId == guildId)
            .Select(t => new { t.GuildId, t.RoleId })
            .ToListAsync(cancellationToken);
        foreach (var type in fromTypes)
        {
            Add(watched, type.GuildId, [type.RoleId]);
        }

        return watched;
    }

    /// <summary>The roles one option makes watched: its restriction and the role it grants.</summary>
    /// <param name="option">The option.</param>
    /// <returns>The distinct role ids, possibly none.</returns>
    public static IEnumerable<long> NamedBy(RsvpOption option) => NamedBy(option.AllowedRoleIds, option.AttendeeRoleId);

    private static IEnumerable<long> NamedBy(long[] allowedRoleIds, long? attendeeRoleId) =>
        attendeeRoleId is { } granted ? allowedRoleIds.Append(granted).Distinct() : allowedRoleIds;

    private static void Add(Dictionary<long, HashSet<long>> watched, long guildId, IEnumerable<long> roleIds)
    {
        foreach (var roleId in roleIds)
        {
            if (!watched.TryGetValue(guildId, out var set))
            {
                set = [];
                watched[guildId] = set;
            }

            set.Add(roleId);
        }
    }
}
