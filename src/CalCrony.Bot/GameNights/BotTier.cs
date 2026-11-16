using System.Collections.Concurrent;
using CalCrony.Contracts.GameNights;
using Discord.WebSocket;

namespace CalCrony.Bot.GameNights;

/// <summary>Works out a Discord member's Game Nights tier on the bot side. The API trusts the bot,
/// so the bot checks live from its own role cache (as it does for signup restrictions): a server
/// manager (Manage Server or the owner) is Admin; otherwise the highest mapped role held. The mapping is
/// cached for a minute. If the mapping cannot be read the member is treated as a plain Member, so
/// only managers act: it fails closed.</summary>
/// <param name="api">The Game Nights API client.</param>
public sealed class BotTier(GameNightsApiClient api)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<long, (DateTimeOffset At, IReadOnlyList<TierRoleDto> Mapping)> cache = new();

    /// <summary>The tier of a guild member.</summary>
    /// <param name="user">The member.</param>
    /// <param name="ct">Cancels the lookup.</param>
    /// <returns>The member's tier.</returns>
    public async Task<GameNightsTier> ResolveAsync(SocketGuildUser user, CancellationToken ct = default)
    {
        var isManager = user.Guild.OwnerId == user.Id || user.GuildPermissions.ManageGuild;
        if (isManager)
        {
            return GameNightsTier.Admin;
        }

        var mapping = await MappingAsync((long)user.Guild.Id, ct);
        return TierFor(isManager: false, user.Roles.Select(r => (long)r.Id).ToList(), mapping);
    }

    /// <summary>The pure rule: managers are Admin, otherwise the highest mapped role held.</summary>
    /// <param name="isManager">Whether the member manages the server.</param>
    /// <param name="heldRoleIds">The member's role ids.</param>
    /// <param name="mapping">The guild's mapping.</param>
    /// <returns>The tier.</returns>
    public static GameNightsTier TierFor(bool isManager, IReadOnlyCollection<long> heldRoleIds, IReadOnlyCollection<TierRoleDto> mapping) =>
        isManager ? GameNightsTier.Admin : TierLogic.Resolve(heldRoleIds, mapping);

    private async Task<IReadOnlyList<TierRoleDto>> MappingAsync(long guildId, CancellationToken ct)
    {
        if (cache.TryGetValue(guildId, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheFor)
        {
            return hit.Mapping;
        }

        var result = await api.GetTierRolesAsync(guildId, ct);
        if (!result.Success || result.Value is null)
        {
            return [];
        }

        cache[guildId] = (DateTimeOffset.UtcNow, result.Value.Roles);
        return result.Value.Roles;
    }
}
