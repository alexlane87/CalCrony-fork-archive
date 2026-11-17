using System.Text.Json;
using CalCrony.Bot.Api;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace CalCrony.Bot.GameNights;

/// <summary>Posts a table's Discord thread and its seat notes. The thread is a public thread on a starter
/// message in the game night's channel (the same thing event discussion threads are; private threads would
/// need a permission the bot's invite does not carry yet).
/// <para>Creating the thread is safe to repeat: the delivery names only the table, the bot reads the table
/// first, and does nothing if it already has a thread, is closed or is gone. The thread id is recorded
/// with the API straight after creation and the post is removed again if that fails, so a retry cannot
/// leave a second thread behind. Seat notes are best-effort and always acknowledge.</para></summary>
/// <param name="client">The Discord client.</param>
/// <param name="api">The CalCrony API client.</param>
/// <param name="gameNights">The Game Nights API client.</param>
/// <param name="logger">The logger.</param>
public sealed class TableDeliveryHandler(
    DiscordSocketClient client, CalCronyApiClient api, GameNightsApiClient gameNights, ILogger<TableDeliveryHandler> logger)
    : IExtensionDeliveryHandler
{
    /// <inheritdoc />
    public bool Handles(DeliveryType type) => type is DeliveryType.CreateTableThread or DeliveryType.TableSeatChange;

    /// <inheritdoc />
    public Task<bool> HandleAsync(DeliveryDto delivery) =>
        delivery.Type == DeliveryType.CreateTableThread ? CreateThreadAsync(delivery) : SeatChangeAsync(delivery);

    private async Task<bool> CreateThreadAsync(DeliveryDto delivery)
    {
        if (Read<CreateTableThreadPayload>(delivery) is not { } payload)
        {
            return true;
        }

        var found = await gameNights.GetTableAsync(payload.GuildId, payload.TableId);
        if (!found.Success)
        {
            if (found.NotFound)
            {
                return true; // the table or its night is gone
            }

            throw new InvalidOperationException($"Could not read table {payload.TableId}: {found.Error}");
        }

        var table = found.Value!;
        if (table.ThreadId is not null || table.Status != TableStatus.Open)
        {
            return true;
        }

        if (await client.GetChannelAsync((ulong)delivery.ChannelId) is not ITextChannel channel)
        {
            throw new InvalidOperationException($"Channel {delivery.ChannelId} not found or not a text channel.");
        }

        var ev = (await api.GetEventAsync(table.EventId)).Value;
        var title = ev?.Title ?? "Game night";
        var message = await channel.SendMessageAsync(embed: TableText.Starter(table, title), allowedMentions: AllowedMentions.None);
        IThreadChannel? thread = null;
        try
        {
            thread = await channel.CreateThreadAsync(
                TableText.ThreadName(table, title), ThreadType.PublicThread, ThreadArchiveDuration.OneWeek, message);
            var recorded = await gameNights.SetTableThreadAsync(payload.GuildId, table.Id, (long)thread.Id);
            if (!recorded.Success)
            {
                throw new InvalidOperationException($"Failed to record table thread: {recorded.Error}");
            }
        }
        catch
        {
            // Without a recorded thread id a retry would make a second one: take this one back first.
            await TryAsync(() => thread?.DeleteAsync() ?? Task.CompletedTask);
            await TryAsync(() => message.DeleteAsync());
            throw;
        }

        // The thread exists and is recorded. Everything from here is the welcome, which is best-effort:
        // the host is addressed first, then the other players, each message pinging only the people it names.
        try
        {
            var (hostText, hostMentions) = TableText.HostPrompt(table);
            await thread.SendMessageAsync(hostText, allowedMentions: Only(hostMentions));
            if (TableText.PlayersWelcome(table) is { } welcome)
            {
                await thread.SendMessageAsync(welcome.Text, allowedMentions: Only(welcome.Mentions));
            }

            // The button for reporting the result, for games the Discord flow can handle.
            var game = await gameNights.GetGameAsync(payload.GuildId, table.GameId);
            if (game.Value is { } reportable && ReportFlow.IsReportable(reportable.Spec))
            {
                await thread.SendMessageAsync(
                    "When you've finished, press **Report game** to record the result.",
                    components: ReportButton(table.Id), allowedMentions: AllowedMentions.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Table {TableId}'s thread exists but its welcome could not be posted.", table.Id);
        }

        return true;
    }

    private async Task<bool> SeatChangeAsync(DeliveryDto delivery)
    {
        if (Read<TableSeatChangePayload>(delivery) is not { } payload)
        {
            return true;
        }

        try
        {
            var found = await gameNights.GetTableAsync(payload.GuildId, payload.TableId);
            if (!found.Success || found.Value is not { ThreadId: { } threadId } table)
            {
                return true; // table gone, or its thread not made yet (the thread's own welcome lists the seats)
            }

            if (await client.GetChannelAsync((ulong)threadId) is not IThreadChannel thread)
            {
                await gameNights.SetTableThreadAsync(payload.GuildId, table.Id, null); // deleted in Discord: forget it
                return true;
            }

            if (payload.Joined && client.GetGuild((ulong)payload.GuildId)?.GetUser((ulong)payload.UserId) is { } member)
            {
                await thread.AddUserAsync(member);
            }

            await thread.SendMessageAsync(
                TableText.SeatChange(payload, table.Seats.Count, table.Size),
                allowedMentions: Only([(ulong)payload.UserId]));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort table seat note failed for table {TableId}.", payload.TableId);
        }

        return true;
    }

    private T? Read<T>(DeliveryDto delivery) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(delivery.PayloadJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Discarding table delivery {DeliveryId} with a malformed payload.", delivery.Id);
            return null;
        }
    }

    /// <summary>The Report game button for a table's thread.</summary>
    /// <param name="tableId">The table.</param>
    /// <returns>The button.</returns>
    public static MessageComponent ReportButton(Guid tableId) =>
        new ComponentBuilder().WithButton("Report game", $"{ReportView.StartPrefix}:{tableId}", ButtonStyle.Success, new Emoji("🏁")).Build();

    private static AllowedMentions Only(IReadOnlyList<ulong> userIds) =>
        new() { AllowedTypes = AllowedMentionTypes.Users, UserIds = [.. userIds] };

    private static async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch
        {
            // Best effort: a stray message beats a duplicate thread.
        }
    }
}
