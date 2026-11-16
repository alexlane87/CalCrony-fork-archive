using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.GameNights;

/// <summary>Venues and their areas (organisers and above write, any member reads), and an event's
/// venue settings. Attaching a venue, toggling an area or editing a venue's numbers re-applies the
/// event's attendee limit through <see cref="EventLimits"/>, so waitlist promotion and the Discord
/// re-render happen exactly as for a normal limit edit.</summary>
public static class VenueEndpoints
{
    /// <summary>Maps the venue routes onto the Game Nights group.</summary>
    /// <param name="g">The <c>/guilds/{guildId}/game-nights</c> group.</param>
    public static void MapVenueEndpoints(this RouteGroupBuilder g)
    {
        g.MapGet("/venues", ListVenues).RequireTier(GameNightsTier.Member);
        g.MapPost("/venues", CreateVenue).RequireTier(GameNightsTier.Organiser);
        g.MapPatch("/venues/{venueId:guid}", UpdateVenue).RequireTier(GameNightsTier.Organiser);
        g.MapDelete("/venues/{venueId:guid}", DeleteVenue).RequireTier(GameNightsTier.Organiser);
        g.MapPost("/venues/{venueId:guid}/areas", CreateArea).RequireTier(GameNightsTier.Organiser);
        g.MapPatch("/venues/{venueId:guid}/areas/{areaId:guid}", UpdateArea).RequireTier(GameNightsTier.Organiser);
        g.MapDelete("/venues/{venueId:guid}/areas/{areaId:guid}", DeleteArea).RequireTier(GameNightsTier.Organiser);

        g.MapGet("/events/{eventId:guid}/venue", GetEventVenue).RequireTier(GameNightsTier.Member);
        g.MapPut("/events/{eventId:guid}/venue", SetEventVenue).RequireTier(GameNightsTier.Organiser);
        g.MapPut("/events/{eventId:guid}/venue/areas/{areaId:guid}", SetEventArea).RequireTier(GameNightsTier.Organiser);
    }

    // ---- venues ----

    private static async Task<IResult> ListVenues(
        long guildId, CalCronyDbContext db, CancellationToken cancellationToken, bool includeRetired = false)
    {
        var venues = await db.GameNightVenues.AsNoTracking()
            .Include(v => v.Areas)
            .Where(v => v.GuildId == guildId && (includeRetired || v.Active))
            .OrderBy(v => v.Name)
            .ToListAsync(cancellationToken);
        return Results.Ok(venues.Select(ToDto).ToList());
    }

    private static async Task<IResult> CreateVenue(
        long guildId, CreateVenueRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (ValidateName(request.Name, VenueLimits.NameLength, "venue name") is { } badName)
        {
            return badName;
        }

        if (request.Address is { Length: > VenueLimits.AddressLength })
        {
            return Bad($"The address can be at most {VenueLimits.AddressLength} characters.");
        }

        if (ValidateLimit(request.DefaultLimit, "default limit") is { } badLimit)
        {
            return badLimit;
        }

        if (await db.GameNightVenues.CountAsync(v => v.GuildId == guildId, cancellationToken) >= VenueLimits.MaxVenuesPerGuild)
        {
            return Bad($"A server can have at most {VenueLimits.MaxVenuesPerGuild} venues.");
        }

        var name = request.Name.Trim();
        if (await db.GameNightVenues.AnyAsync(v => v.GuildId == guildId && v.Name == name, cancellationToken))
        {
            return Results.Conflict(new ErrorResponse($"There is already a venue called \"{name}\"."));
        }

        var venue = new GameNightVenue
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            Name = name,
            Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim(),
            DefaultLimit = request.DefaultLimit,
        };
        db.GameNightVenues.Add(venue);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/guilds/{guildId}/game-nights/venues/{venue.Id}", ToDto(venue));
    }

    private static async Task<IResult> UpdateVenue(
        long guildId, Guid venueId, UpdateVenueRequest request, CalCronyDbContext db, IClock clock,
        CancellationToken cancellationToken)
    {
        if (request.DefaultLimit is not null && request.ClearDefaultLimit)
        {
            return Bad("Choose a default limit or clear it, not both.");
        }

        if (request.Address is not null && request.ClearAddress)
        {
            return Bad("Choose an address or clear it, not both.");
        }

        if (request.Name is not null && ValidateName(request.Name, VenueLimits.NameLength, "venue name") is { } badName)
        {
            return badName;
        }

        if (request.Address is { Length: > VenueLimits.AddressLength })
        {
            return Bad($"The address can be at most {VenueLimits.AddressLength} characters.");
        }

        if (ValidateLimit(request.DefaultLimit, "default limit") is { } badLimit)
        {
            return badLimit;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var venue = await db.GameNightVenues.Include(v => v.Areas)
            .FirstOrDefaultAsync(v => v.Id == venueId && v.GuildId == guildId, cancellationToken);
        if (venue is null)
        {
            return Results.NotFound();
        }

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (await db.GameNightVenues.AnyAsync(v => v.GuildId == guildId && v.Name == name && v.Id != venueId, cancellationToken))
            {
                return Results.Conflict(new ErrorResponse($"There is already a venue called \"{name}\"."));
            }

            venue.Name = name;
        }

        if (request.ClearAddress)
        {
            venue.Address = null;
        }
        else if (request.Address is not null)
        {
            venue.Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();
        }

        if (request.ClearDefaultLimit)
        {
            venue.DefaultLimit = null;
        }
        else if (request.DefaultLimit is not null)
        {
            venue.DefaultLimit = request.DefaultLimit;
        }

        if (request.Active is { } active)
        {
            venue.Active = active;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (await EventLimits.ApplyForVenueAsync(db, venueId, clock, cancellationToken) is { } error)
        {
            return Results.Conflict(new ErrorResponse(error));
        }

        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(ToDto(venue));
    }

    private static async Task<IResult> DeleteVenue(
        long guildId, Guid venueId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var venue = await db.GameNightVenues.FirstOrDefaultAsync(v => v.Id == venueId && v.GuildId == guildId, cancellationToken);
        if (venue is null)
        {
            return Results.NotFound();
        }

        if (await db.GameNightEventExtensions.AnyAsync(x => x.VenueId == venueId, cancellationToken))
        {
            return Results.Conflict(new ErrorResponse("Events have used this venue, so it can't be deleted — retire it instead."));
        }

        db.GameNightVenues.Remove(venue);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // ---- areas ----

    private static async Task<IResult> CreateArea(
        long guildId, Guid venueId, CreateVenueAreaRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (ValidateName(request.Name, VenueLimits.AreaNameLength, "area name") is { } badName)
        {
            return badName;
        }

        if (request.ExtraCapacity < 1 || request.ExtraCapacity > VenueLimits.MaxAttendeeLimit)
        {
            return Bad($"The extra capacity must be between 1 and {VenueLimits.MaxAttendeeLimit}.");
        }

        var venue = await db.GameNightVenues.Include(v => v.Areas)
            .FirstOrDefaultAsync(v => v.Id == venueId && v.GuildId == guildId, cancellationToken);
        if (venue is null)
        {
            return Results.NotFound();
        }

        var name = request.Name.Trim();
        if (venue.Areas.Count >= VenueLimits.MaxAreasPerVenue)
        {
            return Bad($"A venue can have at most {VenueLimits.MaxAreasPerVenue} areas.");
        }

        if (venue.Areas.Any(a => a.Name == name))
        {
            return Results.Conflict(new ErrorResponse($"\"{venue.Name}\" already has an area called \"{name}\"."));
        }

        var area = new GameNightVenueArea { Id = Guid.NewGuid(), VenueId = venueId, Name = name, ExtraCapacity = request.ExtraCapacity };
        db.GameNightVenueAreas.Add(area);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/guilds/{guildId}/game-nights/venues/{venueId}", ToDto(area));
    }

    private static async Task<IResult> UpdateArea(
        long guildId, Guid venueId, Guid areaId, UpdateVenueAreaRequest request, CalCronyDbContext db, IClock clock,
        CancellationToken cancellationToken)
    {
        if (request.Name is not null && ValidateName(request.Name, VenueLimits.AreaNameLength, "area name") is { } badName)
        {
            return badName;
        }

        if (request.ExtraCapacity is { } capacity && (capacity < 1 || capacity > VenueLimits.MaxAttendeeLimit))
        {
            return Bad($"The extra capacity must be between 1 and {VenueLimits.MaxAttendeeLimit}.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var area = await FindAreaAsync(db, guildId, venueId, areaId, cancellationToken);
        if (area is null)
        {
            return Results.NotFound();
        }

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (await db.GameNightVenueAreas.AnyAsync(a => a.VenueId == venueId && a.Name == name && a.Id != areaId, cancellationToken))
            {
                return Results.Conflict(new ErrorResponse($"This venue already has an area called \"{name}\"."));
            }

            area.Name = name;
        }

        if (request.ExtraCapacity is { } newCapacity)
        {
            area.ExtraCapacity = newCapacity;
        }

        if (request.Active is { } active)
        {
            area.Active = active;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (await EventLimits.ApplyForVenueAsync(db, venueId, clock, cancellationToken) is { } error)
        {
            return Results.Conflict(new ErrorResponse(error));
        }

        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(ToDto(area));
    }

    private static async Task<IResult> DeleteArea(
        long guildId, Guid venueId, Guid areaId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var area = await FindAreaAsync(db, guildId, venueId, areaId, cancellationToken);
        if (area is null)
        {
            return Results.NotFound();
        }

        if (await db.GameNightEventAreas.AnyAsync(ea => ea.AreaId == areaId, cancellationToken))
        {
            return Results.Conflict(new ErrorResponse("Events have used this area, so it can't be deleted — retire it instead."));
        }

        db.GameNightVenueAreas.Remove(area);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // ---- an event's venue ----

    private static async Task<IResult> GetEventVenue(
        long guildId, Guid eventId, HttpContext context, TierResolver tiers, CalCronyDbContext db,
        CancellationToken cancellationToken)
    {
        var ev = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId && e.GuildId == guildId, cancellationToken);
        if (ev is null || !await CanSeeAsync(context, tiers, db, guildId, eventId, cancellationToken))
        {
            return Results.NotFound();
        }

        return Results.Ok(await BuildEventVenueAsync(db, eventId, cancellationToken));
    }

    private static async Task<IResult> SetEventVenue(
        long guildId, Guid eventId, SetEventVenueRequest request, HttpContext context, TierResolver tiers,
        CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        if (ValidateLimit(request.LimitOverride, "limit") is { } badLimit)
        {
            return badLimit;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"""SELECT "Id" FROM "Events" WHERE "Id" = {eventId} FOR UPDATE""", cancellationToken);
        var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId && e.GuildId == guildId, cancellationToken);
        if (ev is null || !await CanSeeAsync(context, tiers, db, guildId, eventId, cancellationToken))
        {
            return Results.NotFound();
        }

        GameNightVenue? venue = null;
        var wantedAreas = (request.AreaIdsOn ?? []).Distinct().ToList();
        if (request.VenueId is { } venueId)
        {
            venue = await db.GameNightVenues.Include(v => v.Areas)
                .FirstOrDefaultAsync(v => v.Id == venueId && v.GuildId == guildId, cancellationToken);
            if (venue is null)
            {
                return Bad("That venue does not exist in this server.");
            }

            var existing = await db.GameNightEventExtensions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.EventId == eventId, cancellationToken);
            if (!venue.Active && existing?.VenueId != venueId)
            {
                return Bad("That venue is retired.");
            }

            var usable = venue.Areas.Where(a => a.Active).Select(a => a.Id).ToHashSet();
            if (wantedAreas.Any(id => !usable.Contains(id)))
            {
                return Bad("Every area must be an active area of the chosen venue.");
            }
        }
        else if (wantedAreas.Count > 0)
        {
            return Bad("Areas can only be switched on for an event that has a venue.");
        }

        var extension = await db.GameNightEventExtensions.FirstOrDefaultAsync(x => x.EventId == eventId, cancellationToken);
        if (extension is null)
        {
            extension = new GameNightEventExtension { EventId = eventId };
            db.GameNightEventExtensions.Add(extension);
        }

        extension.VenueId = request.VenueId;
        extension.LimitOverride = request.LimitOverride;
        await db.GameNightEventAreas.Where(ea => ea.EventId == eventId).ExecuteDeleteAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var areaId in wantedAreas)
        {
            db.GameNightEventAreas.Add(new GameNightEventArea { EventId = eventId, AreaId = areaId });
        }

        if (venue is not null)
        {
            // The event's existing location line shows the venue everywhere it already renders
            // (the Discord post, the web, the calendar feed) without touching those surfaces.
            ev.Location = LocationFor(venue);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (await EventLimits.ApplyAsync(db, eventId, clock, cancellationToken) is { } error)
        {
            return Results.Conflict(new ErrorResponse(error));
        }

        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(await BuildEventVenueAsync(db, eventId, cancellationToken));
    }

    private static async Task<IResult> SetEventArea(
        long guildId, Guid eventId, Guid areaId, SetEventAreaRequest request, HttpContext context, TierResolver tiers,
        CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"""SELECT "Id" FROM "Events" WHERE "Id" = {eventId} FOR UPDATE""", cancellationToken);
        var ev = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId && e.GuildId == guildId, cancellationToken);
        if (ev is null || !await CanSeeAsync(context, tiers, db, guildId, eventId, cancellationToken))
        {
            return Results.NotFound();
        }

        var extension = await db.GameNightEventExtensions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.EventId == eventId, cancellationToken);
        if (extension?.VenueId is not { } venueId)
        {
            return Bad("This event has no venue, so it has no areas to switch.");
        }

        var area = await db.GameNightVenueAreas.FirstOrDefaultAsync(a => a.Id == areaId && a.VenueId == venueId, cancellationToken);
        if (area is null)
        {
            return Results.NotFound();
        }

        var isOn = await db.GameNightEventAreas.AnyAsync(ea => ea.EventId == eventId && ea.AreaId == areaId, cancellationToken);
        if (request.On && !isOn)
        {
            if (!area.Active)
            {
                return Bad("That area is retired.");
            }

            db.GameNightEventAreas.Add(new GameNightEventArea { EventId = eventId, AreaId = areaId });
        }
        else if (!request.On && isOn)
        {
            await db.GameNightEventAreas.Where(ea => ea.EventId == eventId && ea.AreaId == areaId).ExecuteDeleteAsync(cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (await EventLimits.ApplyAsync(db, eventId, clock, cancellationToken) is { } error)
        {
            return Results.Conflict(new ErrorResponse(error));
        }

        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(await BuildEventVenueAsync(db, eventId, cancellationToken));
    }

    // ---- helpers ----

    private static async Task<bool> CanSeeAsync(
        HttpContext context, TierResolver tiers, CalCronyDbContext db, long guildId, Guid eventId, CancellationToken cancellationToken)
    {
        var check = await tiers.ResolveAsync(context.User, guildId, cancellationToken);
        return await VisibilityFilter.CanSeeEventAsync(
            db, eventId, context.User.WebUserId() ?? 0, check.Tier, cancellationToken);
    }

    private static async Task<EventVenueDto> BuildEventVenueAsync(CalCronyDbContext db, Guid eventId, CancellationToken cancellationToken)
    {
        var extension = await db.GameNightEventExtensions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.EventId == eventId, cancellationToken);
        var venue = extension?.VenueId is { } venueId
            ? await db.GameNightVenues.AsNoTracking().Include(v => v.Areas).FirstOrDefaultAsync(v => v.Id == venueId, cancellationToken)
            : null;
        var areasOn = await db.GameNightEventAreas.AsNoTracking()
            .Where(ea => ea.EventId == eventId)
            .Select(ea => ea.AreaId)
            .ToListAsync(cancellationToken);
        var effective = await EventLimits.ComputeAsync(db, extension, cancellationToken);
        return new EventVenueDto(
            eventId, venue is null ? null : ToDto(venue), extension?.LimitOverride, areasOn, effective);
    }

    private static async Task<GameNightVenueArea?> FindAreaAsync(
        CalCronyDbContext db, long guildId, Guid venueId, Guid areaId, CancellationToken cancellationToken) =>
        await db.GameNightVenueAreas
            .Join(db.GameNightVenues.Where(v => v.GuildId == guildId && v.Id == venueId),
                a => a.VenueId, v => v.Id, (a, v) => a)
            .FirstOrDefaultAsync(a => a.Id == areaId, cancellationToken);

    private static string LocationFor(GameNightVenue venue)
    {
        var text = string.IsNullOrWhiteSpace(venue.Address) ? venue.Name : $"{venue.Name}, {venue.Address}";
        return text.Length <= FieldLimits.EventLocation ? text : text[..FieldLimits.EventLocation];
    }

    private static VenueDto ToDto(GameNightVenue v) => new(
        v.Id, v.Name, v.Address, v.DefaultLimit, v.Active,
        [.. v.Areas.OrderBy(a => a.Name).Select(ToDto)]);

    private static VenueAreaDto ToDto(GameNightVenueArea a) => new(a.Id, a.Name, a.ExtraCapacity, a.Active);

    private static IResult? ValidateName(string? name, int max, string what) =>
        string.IsNullOrWhiteSpace(name) ? Bad($"The {what} is required.")
        : name.Trim().Length > max ? Bad($"The {what} can be at most {max} characters.")
        : null;

    private static IResult? ValidateLimit(int? limit, string what) =>
        limit is < 1 or > VenueLimits.MaxAttendeeLimit
            ? Bad($"The {what} must be between 1 and {VenueLimits.MaxAttendeeLimit}.")
            : null;

    private static IResult Bad(string message) => Results.BadRequest(new ErrorResponse(message));
}
