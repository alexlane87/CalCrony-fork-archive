using System.Globalization;
using System.Text;
using System.Text.Json;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace CalCrony.Bot.GameNights;

/// <summary>The words the bot posts about a result. Pure, so the mentions and the text are testable.</summary>
public static class ResultText
{
    private const int MessageLimit = 1900;

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

    /// <summary>The post that shows a reported result: who finished where with their answers, the table-wide answers, and
    /// either "confirmed" or who still has to answer.</summary>
    /// <param name="result">The result.</param>
    /// <param name="tableNumber">The table's number.</param>
    /// <returns>The message text (under Discord's limit).</returns>
    public static string Summary(ResultDto result, int tableNumber)
    {
        var text = new StringBuilder();
        text.AppendLine($"🏁 **{result.GameName}, table {tableNumber}**: reported by <@{result.ReporterUserId}>");
        foreach (var player in result.Players)
        {
            var place = player.Placement is { } p ? $"{p}." : "•";
            var answers = player.Values.Values.Concat(player.Score is { } score ? [$"score {score.ToString(CultureInfo.InvariantCulture)}"] : []).ToList();
            text.AppendLine($"{place} {player.DisplayName}{(answers.Count == 0 ? "" : $": {string.Join(", ", answers)}")}");
        }

        foreach (var (key, value) in result.TableValues.OrderBy(kv => kv.Key))
        {
            text.AppendLine($"{Humanise(key)}: {value}");
        }

        text.Append(result.Status switch
        {
            ResultStatus.Confirmed => "✅ Confirmed.",
            ResultStatus.Voided => "↩️ Withdrawn.",
            ResultStatus.Disputed => "⚠️ Disputed: waiting for a moderator.",
            _ => $"Waiting for {string.Join(", ", result.AwaitingUserIds.Select(id => $"<@{id}>"))} to confirm. Press **Confirm** if this is right, **Reject** if it isn't.",
        });
        var nudge = Nudge(result.UnnamedUserIds);
        if (nudge is not null)
        {
            text.Append("\n\n").Append(nudge);
        }

        var all = text.ToString();
        return all.Length <= MessageLimit ? all : all[..(MessageLimit - 1)] + "…";
    }

    /// <summary>Asks members whose server nickname does not read as "First L" to fix it.</summary>
    /// <param name="userIds">The members with no usable name.</param>
    /// <returns>The nudge, or null when there is nobody to nudge.</returns>
    public static string? Nudge(IReadOnlyList<long> userIds) =>
        userIds.Count == 0
            ? null
            : $"{string.Join(" ", userIds.Select(id => $"<@{id}>"))}: I can't read a name like \"Alex P\" from your server nickname, so the result shows you as \"{DisplayNames.UnnamedPlayer}\". " +
              "If you'd like your name shown, set your server nickname to your first name and the first letter of your surname (for example **Alex P**).";

    /// <summary>The reminder for people who have not answered.</summary>
    /// <param name="result">The result.</param>
    /// <param name="tableNumber">The table's number.</param>
    /// <returns>The text.</returns>
    public static string Reminder(ResultDto result, int tableNumber) =>
        $"⏰ Still waiting for {string.Join(", ", result.AwaitingUserIds.Select(id => $"<@{id}>"))} to confirm the **{result.GameName}, table {tableNumber}** result above. " +
        "Press **Confirm** if it's right, **Reject** if it isn't.";

    /// <summary>The line posted when a result settles.</summary>
    /// <param name="result">The result.</param>
    /// <param name="tableNumber">The table's number.</param>
    /// <returns>The text, or null for a state that has no notice.</returns>
    public static string? Settled(ResultDto result, int tableNumber) => result.Status switch
    {
        ResultStatus.Confirmed => $"✅ The **{result.GameName}, table {tableNumber}** result is confirmed.",
        ResultStatus.Voided => $"↩️ The **{result.GameName}, table {tableNumber}** result was withdrawn. The table can report again.",
        _ => null,
    };

    /// <summary>The Confirm and Reject buttons under a pending result.</summary>
    /// <param name="resultId">The result.</param>
    /// <returns>The buttons.</returns>
    public static MessageComponent Buttons(Guid resultId) =>
        new ComponentBuilder()
            .WithButton("Confirm", $"gnres:ok:{resultId}", ButtonStyle.Success, new Emoji("✅"))
            .WithButton("Reject", $"gnres:no:{resultId}", ButtonStyle.Danger, new Emoji("⚠️"))
            .Build();

    private static string Humanise(string key)
    {
        var words = key.Replace('_', ' ');
        return words.Length == 0 ? key : char.ToUpperInvariant(words[0]) + words[1..];
    }
}

/// <summary>Posts the Game Nights result notices, all of which arrive through the outbox: the result for the others to
/// confirm (with Confirm and Reject buttons, mentioning only the people who have to answer), the single reminder for those
/// who have not, the "confirmed" or "withdrawn" line when it settles, and the dispute notice for the organisers.
/// <para>Each reads the result from the API when it runs, so it is safe to repeat and always describes the result as it is
/// now. Posting the summary is made idempotent by recording the message id with the API straight after posting (and taking
/// the post back if that fails); the other notices are best-effort and always acknowledge.</para></summary>
/// <param name="client">The Discord client.</param>
/// <param name="gameNights">The Game Nights API client.</param>
/// <param name="logger">The logger.</param>
public sealed class ResultDeliveryHandler(DiscordSocketClient client, GameNightsApiClient gameNights, ILogger<ResultDeliveryHandler> logger)
    : IExtensionDeliveryHandler
{
    /// <inheritdoc />
    public bool Handles(DeliveryType type) =>
        type is DeliveryType.ResultDisputed or DeliveryType.ResultSubmitted or DeliveryType.ResultReminder or DeliveryType.ResultSettled;

    /// <inheritdoc />
    public async Task<bool> HandleAsync(DeliveryDto delivery) => delivery.Type switch
    {
        DeliveryType.ResultDisputed => await DisputedAsync(delivery),
        DeliveryType.ResultSubmitted => await SubmittedAsync(delivery),
        DeliveryType.ResultReminder => await ReminderAsync(delivery),
        _ => await SettledAsync(delivery),
    };

    private async Task<bool> SubmittedAsync(DeliveryDto delivery)
    {
        if (Read<ResultNoticePayload>(delivery) is not { } payload)
        {
            return true;
        }

        var loaded = await LoadAsync(payload);
        if (loaded is null)
        {
            return true; // result or table gone
        }

        var (result, table) = loaded.Value;
        if (result.SummaryMessageId is not null)
        {
            return true; // already posted
        }

        var channel = await WhereAsync(table, delivery)
            ?? throw new InvalidOperationException($"No channel to post result {result.Id} in.");
        var notify = result.AwaitingUserIds.Concat(result.UnnamedUserIds).Distinct().Select(id => (ulong)id).ToList();
        var message = await channel.SendMessageAsync(
            ResultText.Summary(result, table.Number),
            components: result.Status == ResultStatus.Pending ? ResultText.Buttons(result.Id) : null,
            allowedMentions: new AllowedMentions { AllowedTypes = AllowedMentionTypes.Users, UserIds = notify });

        var recorded = await gameNights.SetResultMessageAsync(payload.GuildId, result.Id, (long)message.Id);
        if (!recorded.Success)
        {
            // Without the message id a retry would post it twice: take this post back first.
            try
            {
                await message.DeleteAsync();
            }
            catch
            {
                // Best effort.
            }

            throw new InvalidOperationException($"Failed to record the result post: {recorded.Error}");
        }

        return true;
    }

    private async Task<bool> ReminderAsync(DeliveryDto delivery)
    {
        if (Read<ResultNoticePayload>(delivery) is not { } payload)
        {
            return true;
        }

        try
        {
            if (await LoadAsync(payload) is not var (result, table) || result.Status != ResultStatus.Pending || result.AwaitingUserIds.Count == 0)
            {
                return true; // answered in the meantime
            }

            if (await WhereAsync(table, delivery) is { } channel)
            {
                await channel.SendMessageAsync(
                    ResultText.Reminder(result, table.Number),
                    allowedMentions: new AllowedMentions { AllowedTypes = AllowedMentionTypes.Users, UserIds = [.. result.AwaitingUserIds.Select(id => (ulong)id)] });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort result reminder failed for result {ResultId}.", payload.ResultId);
        }

        return true;
    }

    private async Task<bool> SettledAsync(DeliveryDto delivery)
    {
        if (Read<ResultNoticePayload>(delivery) is not { } payload)
        {
            return true;
        }

        try
        {
            if (await LoadAsync(payload) is not var (result, table) || ResultText.Settled(result, table.Number) is not { } line)
            {
                return true;
            }

            if (await WhereAsync(table, delivery) is { } channel)
            {
                await channel.SendMessageAsync(line, allowedMentions: AllowedMentions.None);

                // The result post no longer needs its buttons.
                if (result.SummaryMessageId is { } messageId && await channel.GetMessageAsync((ulong)messageId) is IUserMessage post)
                {
                    await post.ModifyAsync(m =>
                    {
                        m.Content = ResultText.Summary(result, table.Number);
                        m.Components = new ComponentBuilder().Build();
                        m.AllowedMentions = AllowedMentions.None;
                    });
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort result settled notice failed for result {ResultId}.", payload.ResultId);
        }

        return true;
    }

    private async Task<bool> DisputedAsync(DeliveryDto delivery)
    {
        if (Read<ResultDisputedPayload>(delivery) is not { } payload)
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

            if (await WhereAsync(table, delivery) is { } channel)
            {
                await channel.SendMessageAsync(
                    ResultText.Disputed(payload, table.GameName, table.Number),
                    allowedMentions: new AllowedMentions
                    {
                        AllowedTypes = AllowedMentionTypes.Users | AllowedMentionTypes.Roles,
                        UserIds = [(ulong)payload.RejectedByUserId],
                        RoleIds = [.. payload.NotifyRoleIds.Select(id => (ulong)id)],
                    });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort dispute notice failed for result {ResultId}.", payload.ResultId);
        }

        return true;
    }

    /// <summary>Reads the result and its table; null when either is gone, and throws when the API is unreachable so
    /// the delivery is retried.</summary>
    private async Task<(ResultDto Result, TableDto Table)?> LoadAsync(ResultNoticePayload payload)
    {
        var result = await gameNights.GetResultAsync(payload.GuildId, payload.ResultId);
        if (!result.Success)
        {
            return result.NotFound ? null : throw new InvalidOperationException($"Could not read result {payload.ResultId}: {result.Error}");
        }

        var table = await gameNights.GetTableAsync(payload.GuildId, payload.TableId);
        if (!table.Success)
        {
            return table.NotFound ? null : throw new InvalidOperationException($"Could not read table {payload.TableId}: {table.Error}");
        }

        return (result.Value!, table.Value!);
    }

    /// <summary>Where the table is talking: its thread, else the night's channel.</summary>
    private async Task<IMessageChannel?> WhereAsync(TableDto table, DeliveryDto delivery)
    {
        IMessageChannel? where = table.ThreadId is { } thread ? await client.GetChannelAsync((ulong)thread) as IMessageChannel : null;
        return where ?? await client.GetChannelAsync((ulong)delivery.ChannelId) as IMessageChannel;
    }

    private T? Read<T>(DeliveryDto delivery) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(delivery.PayloadJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Discarding result delivery {DeliveryId} with a malformed payload.", delivery.Id);
            return null;
        }
    }
}
