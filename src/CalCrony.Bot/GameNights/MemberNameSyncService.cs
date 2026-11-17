using CalCrony.Contracts.GameNights;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CalCrony.Bot.GameNights;

/// <summary>Keeps the API's "First L" names for the people taking part in Game Nights. Every few minutes, for each
/// server it is in, the bot asks the API which participants it needs a name for (people at tables, in results or
/// answering, whose name is missing or stale), works "First L" out of each one's server nickname, and pushes only
/// that. The raw nickname never leaves the bot. A member who is no longer in the server is reported as having left.
/// <para>Does nothing unless Game Nights is on.</para></summary>
/// <param name="client">The Discord client.</param>
/// <param name="gameNights">The Game Nights API client.</param>
/// <param name="configuration">The application configuration.</param>
/// <param name="logger">The logger.</param>
public sealed class MemberNameSyncService(
    DiscordSocketClient client, GameNightsApiClient gameNights, IConfiguration configuration, ILogger<MemberNameSyncService> logger)
    : BackgroundService
{
    /// <summary>Turns what Discord says about a member into what the API may store: "First L" worked out from the
    /// server nickname (or, with no nickname, the display name they chose themselves), null when it does not follow the
    /// convention, and "left" when the member is gone. The username is never used.</summary>
    /// <param name="userId">The member.</param>
    /// <param name="present">Whether the member is still in the server.</param>
    /// <param name="nickname">Their server nickname, if any.</param>
    /// <param name="globalName">Their chosen global display name, if any.</param>
    /// <returns>What to push.</returns>
    public static MemberNameDto ToDto(long userId, bool present, string? nickname, string? globalName) =>
        !present
            ? new MemberNameDto(userId, null, Left: true)
            : new MemberNameDto(userId, DisplayNames.FromNickname(nickname) ?? (string.IsNullOrWhiteSpace(nickname) ? DisplayNames.FromNickname(globalName) : null));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.IsGameNightsEnabled())
        {
            return;
        }

        var minutes = Math.Clamp(configuration.GetValue("GameNights:NameSyncMinutes", 10), 1, 120);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        do
        {
            if (client.ConnectionState == ConnectionState.Connected)
            {
                foreach (var guild in client.Guilds)
                {
                    try
                    {
                        await SyncAsync(guild, stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Name sync failed for guild {GuildId}.", guild.Id);
                    }
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncAsync(SocketGuild guild, CancellationToken ct)
    {
        var wanted = await gameNights.GetWantedNamesAsync((long)guild.Id, ct);
        if (!wanted.Success || wanted.Value is null || wanted.Value.UserIds.Count == 0)
        {
            return; // an API without Game Nights (404) or nothing to do
        }

        var names = new List<MemberNameDto>();
        foreach (var id in wanted.Value.UserIds)
        {
            IGuildUser? user = guild.GetUser((ulong)id);
            if (user is null)
            {
                try
                {
                    user = await ((IGuild)guild).GetUserAsync((ulong)id);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not look up member {UserId} in guild {GuildId}.", id, guild.Id);
                    continue; // unknown is not the same as gone: try again next round
                }
            }

            names.Add(ToDto(id, user is not null, user?.Nickname, user?.GlobalName));
        }

        if (names.Count > 0)
        {
            await gameNights.PutMemberNamesAsync((long)guild.Id, names, ct);
        }
    }
}
