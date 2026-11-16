namespace CalCrony.Contracts.GameNights;

/// <summary>The Game Nights permission tiers, lowest first. Ordering is meaningful: a caller may do
/// anything a lower tier may. Serialized as the number (Contracts has no string-enum converter).</summary>
public enum GameNightsTier
{
    /// <summary>Anyone in a server CalCrony is in.</summary>
    Member = 0,

    /// <summary>Runs game nights: venues, tables, games.</summary>
    Organiser = 1,

    /// <summary>Handles disputes and corrections.</summary>
    Moderator = 2,

    /// <summary>Configures Game Nights for the server, including the role-to-tier mapping.</summary>
    Admin = 3,
}

/// <summary>One Discord role mapped to a tier.</summary>
/// <param name="RoleId">The Discord role id.</param>
/// <param name="Tier">The tier a holder of the role gets (never <see cref="GameNightsTier.Member"/>,
/// which is everyone's default).</param>
public record TierRoleDto(long RoleId, GameNightsTier Tier);

/// <summary>A guild's role-to-tier mapping.</summary>
/// <param name="Roles">The mapped roles.</param>
public record TierRolesResponse(IReadOnlyList<TierRoleDto> Roles);

/// <summary>Replaces a guild's role-to-tier mapping wholesale.</summary>
/// <param name="Roles">The full mapping; an empty list removes it (everyone is a Member).</param>
public record PutTierRolesRequest(IReadOnlyList<TierRoleDto> Roles);

/// <summary>The caller's tier in a guild.</summary>
/// <param name="Tier">The tier, or Member when it could not be confirmed.</param>
/// <param name="Verified">False when the role snapshot could not answer (the bot has not synced
/// recently), in which case <paramref name="Tier"/> is the safe default and higher-tier actions are
/// refused until the bot syncs.</param>
public record MyTierResponse(GameNightsTier Tier, bool Verified);

/// <summary>Pure tier rules shared by the API, bot and web.</summary>
public static class TierLogic
{
    /// <summary>The highest tier among the mapped roles the member holds; Member when none.</summary>
    /// <param name="heldRoleIds">The roles the member holds.</param>
    /// <param name="mapping">The guild's role-to-tier mapping.</param>
    /// <returns>The member's tier.</returns>
    public static GameNightsTier Resolve(IReadOnlyCollection<long> heldRoleIds, IReadOnlyCollection<TierRoleDto> mapping)
    {
        var held = heldRoleIds as IReadOnlySet<long> ?? heldRoleIds.ToHashSet();
        var tier = GameNightsTier.Member;
        foreach (var entry in mapping)
        {
            if (entry.Tier > tier && held.Contains(entry.RoleId))
            {
                tier = entry.Tier;
            }
        }

        return tier;
    }
}

/// <summary>Error codes the Game Nights API adds to <c>ErrorResponse.Code</c>.</summary>
public static class GameNightsErrorCodes
{
    /// <summary>The caller's tier could not be confirmed (409): the role snapshot is stale or the
    /// mapped roles have not been checked yet. Retry once the bot has synced, or act from Discord.</summary>
    public const string TierUnverifiable = "tier_unverifiable";
}
