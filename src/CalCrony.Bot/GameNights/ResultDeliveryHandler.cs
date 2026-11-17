using System.Text.Json;
using CalCrony.Bot.Api;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace CalCrony.Bot.GameNights;

/// <summary>The words the bot posts about a disputed result. Pure, so the mentions are testable.</summary>
public static class ResultText
{
    /// <summary>"⚠️ @player says the result for Root, table 2 is wrong. @organisers: a moderator can settle it."</summary>
    /// <param name="payload">What was disputed and by whom.</param>
    /// <param name="gameName">The game's name.</param>
    /// <param name="tableNumber">The table's number.</param>
    /// <returns>The text; the callers pin the allowed mentions to the rejector and the organiser roles only.</returns>
    public static string Disputed(ResultDisputedPayload payload, string gameName, int tableNumber)
    {
        var who = payload.NotifyRoleIds.Count == 0 ? "Organisers" : string.Join(" ", payload.NotifyRoleIds.Select(id => $"<@&{id}>"));
        return $"⚠️ <@{payload.RejectedByUserId}> says the result for **{gameName}, table {tableNumber}** is wrong. " +
               $"{who}: please have a look. A moderator can settle it or withdraw it so the table can report again.";
    }
}

/// <summary>Posts the Game Nights result notices: when a player rejects a reported result, the bot says so where the
/// table is talking (its thread, or the night's channel) and pings the organiser roles. Best-effort and always
/// acknowledges: a missed notice must not block anything, and the dispute itself is already recorded.</summary>
/// <param name="client">The Discord client.</param>
/// <param name="gameNights">The Game Nights API client.</param>
/// <param name="logger">The logger.</param>
public sealed class ResultDeliveryHandler(DiscordSocketClient client, GameNightsApiClient gameNights, ILogger<ResultDeliveryHandler> logger)
    : IExtensionDeliveryHandler
{
    /// <inheritdoc />
    public bool Handles(DeliveryType type) => type == DeliveryType.ResultDisputed;

    /// <inheritdoc />
    public async Task<bool> HandleAsync(DeliveryDto delivery)
    {
        ResultDisputedPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ResultDisputedPayload>(delivery.PayloadJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Discarding result delivery {DeliveryId} with a malformed payload.", delivery.Id);
            return true;
        }

        if (payload is null)
        {
            return true;
        }

        try
        {
            var table = (await gameNights.GetTableAsync(payload.GuildId, payload.TableId)).Value;
            if (table is null)
            {
                return true;
            }

            IMessageChannel? where = table.ThreadId is { } thread ? await client.GetChannelAsync((ulong)thread) as IMessageChannel : null;
            where ??= await client.GetChannelAsync((ulong)delivery.ChannelId) as IMessageChannel;
            if (where is null)
            {
                return true;
            }

            await where.SendMessageAsync(
                ResultText.Disputed(payload, table.GameName, table.Number),
                allowedMentions: new AllowedMentions
                {
                    AllowedTypes = AllowedMentionTypes.Users | AllowedMentionTypes.Roles,
                    UserIds = [(ulong)payload.RejectedByUserId],
                    RoleIds = [.. payload.NotifyRoleIds.Select(id => (ulong)id)],
                });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort dispute notice failed for result {ResultId}.", payload.ResultId);
        }

        return true;
    }
}
