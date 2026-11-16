using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.GameNights;

/// <summary>Turns a venue's settings into CalCrony's own attendee limit. The limit lives on the
/// event's attending RSVP option and the waitlist is promoted by the core RsvpPolicy, so this never
/// writes the column and walks away: it sets the capacity the way the event edit does (refusing to
/// go below the seats already taken), promotes the waitlist, and queues the Discord re-render.
/// Callers must have a transaction open.</summary>
internal static class EventLimits
{
    /// <summary>Works out an event's effective limit from its extension row.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="extension">The event's extension row, or null (no venue, no override).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The limit, its source and a description.</returns>
    public static async Task<EffectiveLimitDto> ComputeAsync(
        CalCronyDbContext db, GameNightEventExtension? extension, CancellationToken cancellationToken)
    {
        if (extension is null)
        {
            return VenueLimitRules.Compute(null, null, null, []);
        }

        var venue = extension.VenueId is { } venueId
            ? await db.GameNightVenues.AsNoTracking().FirstOrDefaultAsync(v => v.Id == venueId, cancellationToken)
            : null;
        var areasOn = venue is null
            ? []
            : await db.GameNightEventAreas
                .Where(ea => ea.EventId == extension.EventId)
                .Join(db.GameNightVenueAreas.Where(a => a.Active && a.VenueId == venue.Id),
                    ea => ea.AreaId, a => a.Id, (ea, a) => new { a.Name, a.ExtraCapacity })
                .OrderBy(a => a.Name)
                .ToListAsync(cancellationToken);
        return VenueLimitRules.Compute(
            extension.LimitOverride, venue?.Name, venue?.DefaultLimit,
            [.. areasOn.Select(a => (a.Name, a.ExtraCapacity))]);
    }

    /// <summary>Applies one event's effective limit to the core event. Locks the event row first
    /// (the same lock the RSVP and edit paths take), so a concurrent RSVP cannot race the new
    /// capacity. Skips events that are not live and events with no venue and no override.</summary>
    /// <param name="db">The database context (transaction open; extension changes already saved).</param>
    /// <param name="eventId">The event.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Null on success or no-op; otherwise the message for a 409 (the new limit is below
    /// the seats already taken).</returns>
    public static async Task<string?> ApplyAsync(
        CalCronyDbContext db, Guid eventId, IClock clock, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"""SELECT "Id" FROM "Events" WHERE "Id" = {eventId} FOR UPDATE""", cancellationToken);
        var ev = await db.Events
            .Include(e => e.Options)
            .Include(e => e.Rsvps)
            .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);
        if (ev is null || ev.Status is not (EventStatus.Scheduled or EventStatus.Started))
        {
            return null;
        }

        var extension = await db.GameNightEventExtensions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.EventId == eventId, cancellationToken);
        var effective = await ComputeAsync(db, extension, cancellationToken);
        if (effective.Source == LimitSource.None || RsvpPolicy.AttendingOption(ev.Options) is not { } attending)
        {
            return null;
        }

        if (attending.Capacity == effective.Limit)
        {
            return null;
        }

        if (RsvpPolicy.CapacityBelowSeated(ev, attending, effective.Limit) is { } overCapacity)
        {
            return $"{ev.Title}: {overCapacity}";
        }

        attending.Capacity = effective.Limit;

        // Raising the limit frees seats: seat the waitlist in queue order, exactly as an edit does.
        await RsvpPolicy.PromoteAsync(db, ev, clock, cancellationToken);
        await EnqueueEmbedSyncAsync(db, ev, clock, cancellationToken);
        await LiveListSync.EnqueueSyncForGuildAsync(db, ev.GuildId, clock.GetCurrentInstant(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    /// <summary>Re-applies the limit of every live event that uses a venue and has no override
    /// (after a venue or area edit changed the numbers).</summary>
    /// <param name="db">The database context (transaction open; the edit already saved).</param>
    /// <param name="venueId">The venue.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Null on success; otherwise the first 409 message.</returns>
    public static async Task<string?> ApplyForVenueAsync(
        CalCronyDbContext db, Guid venueId, IClock clock, CancellationToken cancellationToken)
    {
        var eventIds = await db.GameNightEventExtensions
            .Where(x => x.VenueId == venueId && x.LimitOverride == null)
            .Select(x => x.EventId)
            .ToListAsync(cancellationToken);
        foreach (var eventId in eventIds)
        {
            if (await ApplyAsync(db, eventId, clock, cancellationToken) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>Queues a re-render of the event's Discord post. Unlike the core helper this does not
    /// skip bot callers: the bot's venue buttons change data the bot has not rendered itself.
    /// Coalesces with an identical pending sync.</summary>
    private static async Task EnqueueEmbedSyncAsync(
        CalCronyDbContext db, Event ev, IClock clock, CancellationToken cancellationToken)
    {
        if (ev.MessageId is null)
        {
            return;
        }

        var payloadJson = JsonSerializer.Serialize(new SyncEventMessagePayload(ev.Id));
        var alreadyQueued = await db.Deliveries.AnyAsync(
            d => d.Type == DeliveryType.SyncEventMessage
                 && d.Status == DeliveryStatus.Pending
                 && d.PayloadJson == payloadJson,
            cancellationToken);
        if (alreadyQueued)
        {
            return;
        }

        var now = clock.GetCurrentInstant();
        db.Deliveries.Add(new Delivery
        {
            Id = Guid.NewGuid(),
            Type = DeliveryType.SyncEventMessage,
            ChannelId = ev.ChannelId,
            PayloadJson = payloadJson,
            DueAt = now,
            Status = DeliveryStatus.Pending,
            CreatedAt = now,
        });
    }
}
