using CalCrony.Api.Data;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;

namespace CalCrony.Api.GameNights;

/// <summary>What shows for a member.</summary>
/// <param name="DisplayName">"First L", "Former member" or "Unnamed player".</param>
/// <param name="Unnamed">True when the member is still in the server but the bot has no usable "First L" for them
/// (and no one-off name was given), so the bot should nudge them to fix their nickname.</param>
public readonly record struct NameInfo(string DisplayName, bool Unnamed);

/// <summary>The one place a member's displayed name is decided. The API never reads Discord: the bot works a
/// "First L" out of each participant's server nickname and pushes only that. This service turns the stored copy
/// (or a one-off name typed for a single report, which is used for that response and never stored) into the text
/// shown. A member who has left shows as "Former member".</summary>
public static class NameService
{
    /// <summary>How old a matched name may get before the bot is asked to look again.</summary>
    public static readonly TimeSpan MatchedMaxAge = TimeSpan.FromHours(12);

    /// <summary>How old an unmatched name may get before the bot is asked to look again: short, so a member who
    /// fixes their nickname is picked up quickly.</summary>
    public static readonly TimeSpan UnmatchedMaxAge = TimeSpan.FromHours(1);

    /// <summary>Resolves what to show for some members.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="userIds">The members.</param>
    /// <param name="overrides">One-off names for this response only (each already tidied), or null.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What to show for each member.</returns>
    public static async Task<Dictionary<long, NameInfo>> ResolveAsync(
        CalCronyDbContext db, long guildId, IReadOnlyCollection<long> userIds, IReadOnlyDictionary<long, string>? overrides,
        CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();
        var rows = await db.GameNightMemberNames.AsNoTracking()
            .Where(n => n.GuildId == guildId && ids.Contains(n.UserId))
            .ToDictionaryAsync(n => n.UserId, cancellationToken);

        var result = new Dictionary<long, NameInfo>();
        foreach (var id in ids)
        {
            rows.TryGetValue(id, out var row);
            if (row is { Left: true })
            {
                result[id] = new NameInfo(DisplayNames.FormerMember, false);
            }
            else if (row?.DisplayName is { } stored)
            {
                result[id] = new NameInfo(stored, false);
            }
            else if (overrides is not null && overrides.TryGetValue(id, out var typed) && DisplayNames.Tidy(typed) is { } tidy)
            {
                result[id] = new NameInfo(tidy, false);
            }
            else
            {
                result[id] = new NameInfo(DisplayNames.UnnamedPlayer, true);
            }
        }

        return result;
    }
}
