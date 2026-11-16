using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.GameNights;

/// <summary>Queues Game Nights work for the bot through the existing delivery outbox. Rows are added to the
/// caller's unit of work and saved with it, so a request either does its change and queues the post, or does
/// neither.</summary>
internal static class GameNightOutbox
{
    /// <summary>Queues one delivery.</summary>
    /// <typeparam name="TPayload">The payload type for <paramref name="type"/>.</typeparam>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="type">The delivery type.</param>
    /// <param name="channelId">The channel the delivery is for; 0 when it is not about a channel.</param>
    /// <param name="payload">The payload.</param>
    public static void Enqueue<TPayload>(CalCronyDbContext db, IClock clock, DeliveryType type, long channelId, TPayload payload)
    {
        var now = clock.GetCurrentInstant();
        db.Deliveries.Add(new Delivery
        {
            Id = Guid.NewGuid(),
            Type = type,
            ChannelId = channelId,
            PayloadJson = JsonSerializer.Serialize(payload),
            DueAt = now,
            Status = DeliveryStatus.Pending,
            CreatedAt = now,
        });
    }
}
