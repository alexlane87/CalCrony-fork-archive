using System.Text.Json;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace CalCrony.Bot.GameNights;

/// <summary>Posts the website's requests about game roles: make the role for a game, and give or take it for a
/// member. Making a role is safe to repeat (it reads the game first and stops if the game already has a role)
/// and reports what happened back through the game's role status, so a permission problem shows up on the
/// website instead of vanishing. A refusal from Discord is a final answer, not something to retry.</summary>
/// <param name="client">The Discord client.</param>
/// <param name="gameNights">The Game Nights API client.</param>
/// <param name="follow">The follow service.</param>
/// <param name="logger">The logger.</param>
public sealed class GameRoleDeliveryHandler(
    DiscordSocketClient client, GameNightsApiClient gameNights, GameFollowService follow, ILogger<GameRoleDeliveryHandler> logger)
    : IExtensionDeliveryHandler
{
    /// <inheritdoc />
    public bool Handles(DeliveryType type) => type is DeliveryType.CreateGameRole or DeliveryType.GameRoleFollow;

    /// <inheritdoc />
    public Task<bool> HandleAsync(DeliveryDto delivery) =>
        delivery.Type == DeliveryType.CreateGameRole ? CreateRoleAsync(delivery) : FollowAsync(delivery);

    private async Task<bool> CreateRoleAsync(DeliveryDto delivery)
    {
        if (Read<CreateGameRolePayload>(delivery) is not { } payload)
        {
            return true;
        }

        var found = await gameNights.GetGameAsync(payload.GuildId, payload.GameId);
        if (!found.Success)
        {
            if (found.NotFound)
            {
                return true;
            }

            throw new InvalidOperationException($"Could not read game {payload.GameId}: {found.Error}");
        }

        if (found.Value!.Spec.RoleId is not null)
        {
            return true; // already has one: nothing to make
        }

        var guild = client.GetGuild((ulong)payload.GuildId);
        if (guild is null)
        {
            throw new InvalidOperationException($"Guild {payload.GuildId} is not available yet.");
        }

        if (GameRoleChecks.CannotCreate(guild.CurrentUser.GuildPermissions.ManageRoles) is { } warning)
        {
            await Report(payload, new SetGameRoleStatusRequest(null, GameRoleState.Failed, warning));
            return true;
        }

        IRole role;
        try
        {
            role = await guild.CreateRoleAsync(payload.Name, GuildPermissions.None, isHoisted: false, isMentionable: true);
        }
        catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            logger.LogWarning(ex, "Discord refused to create the role for game {GameId}.", payload.GameId);
            await Report(payload, new SetGameRoleStatusRequest(null, GameRoleState.Failed,
                "Discord refused to create the role: the bot's role needs Manage Roles."));
            return true;
        }

        var recorded = await gameNights.SetGameRoleStatusAsync(
            payload.GuildId, payload.GameId, new SetGameRoleStatusRequest((long)role.Id, GameRoleState.None));
        if (!recorded.Success)
        {
            // Without the role id on the game a retry would make a second role: take this one back first.
            try
            {
                await role.DeleteAsync();
            }
            catch
            {
                // Best effort: a stray role beats a duplicate.
            }

            throw new InvalidOperationException($"Failed to record the new role: {recorded.Error}");
        }

        return true;
    }

    private async Task<bool> FollowAsync(DeliveryDto delivery)
    {
        if (Read<GameRoleFollowPayload>(delivery) is not { } payload)
        {
            return true;
        }

        try
        {
            var found = await gameNights.GetGameAsync(payload.GuildId, payload.GameId);
            if (found.Success && found.Value is { } game && game.Spec.RoleId == payload.RoleId)
            {
                await follow.SetForAsync(payload.GuildId, payload.UserId, game, payload.Follow);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort follow change failed for game {GameId}.", payload.GameId);
        }

        return true;
    }

    private Task Report(CreateGameRolePayload payload, SetGameRoleStatusRequest request) =>
        gameNights.SetGameRoleStatusAsync(payload.GuildId, payload.GameId, request);

    private T? Read<T>(DeliveryDto delivery) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(delivery.PayloadJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Discarding game-role delivery {DeliveryId} with a malformed payload.", delivery.Id);
            return null;
        }
    }
}
