using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.GameNights;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests.GameNights;

/// <summary>The effective-limit rule (no database).</summary>
public class VenueLimitRulesTests
{
    [Fact]
    public void An_override_beats_the_venue()
    {
        var result = VenueLimitRules.Compute(6, "Hall", 30, [("upstairs", 12)]);

        Assert.Equal(6, result.Limit);
        Assert.Equal(LimitSource.Override, result.Source);
    }

    [Fact]
    public void A_venue_adds_the_areas_that_are_on_to_its_default()
    {
        var result = VenueLimitRules.Compute(null, "Hall", 30, [("upstairs", 12), ("garden", 8)]);

        Assert.Equal(50, result.Limit);
        Assert.Equal(LimitSource.Venue, result.Source);
        Assert.Equal("Hall 30 + upstairs 12 + garden 8 = 50", result.Explanation);
    }

    [Fact]
    public void A_venue_with_no_default_means_no_limit()
    {
        var result = VenueLimitRules.Compute(null, "Field", null, [("tent", 20)]);

        Assert.Null(result.Limit);
        Assert.Equal(LimitSource.Venue, result.Source);
    }

    [Fact]
    public void No_venue_and_no_override_leaves_the_event_alone()
    {
        var result = VenueLimitRules.Compute(null, null, null, []);

        Assert.Equal(LimitSource.None, result.Source);
        Assert.Null(result.Explanation);
    }
}

/// <summary>Step 2: venues, areas and the event limit they drive. A web manager is Admin tier (so it
/// can act as an organiser); a plain web member is the below-organiser caller.</summary>
public class GameNightsVenueApiTests(GameNightsFixture fixture) : IClassFixture<GameNightsFixture>
{
    private HttpClient Bot => fixture.Client;

    private static long nextGuild = 53000;

    private static long NewGuildId() => Interlocked.Increment(ref nextGuild);

    private sealed record Arrangement(long Guild, HttpClient Organiser, HttpClient Member, VenueDto Venue, VenueAreaDto Upstairs);

    [Fact]
    public async Task Organisers_manage_venues_and_members_can_only_read_them()
    {
        var a = await ArrangeAsync();

        var list = await a.Member.GetFromJsonAsync<List<VenueDto>>($"/guilds/{a.Guild}/game-nights/venues");
        var venue = Assert.Single(list!);
        Assert.Equal("The Hall", venue.Name);
        Assert.Equal(10, venue.DefaultLimit);
        Assert.Equal("Upstairs", Assert.Single(venue.Areas).Name);

        var forbidden = await a.Member.PostAsJsonAsync($"/guilds/{a.Guild}/game-nights/venues", new CreateVenueRequest("Nope"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var forbiddenArea = await a.Member.PostAsJsonAsync(
            $"/guilds/{a.Guild}/game-nights/venues/{venue.Id}/areas", new CreateVenueAreaRequest("Nope", 3));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenArea.StatusCode);
    }

    [Fact]
    public async Task Venue_input_is_validated()
    {
        var a = await ArrangeAsync();
        var venues = $"/guilds/{a.Guild}/game-nights/venues";

        Assert.Equal(HttpStatusCode.BadRequest, (await a.Organiser.PostAsJsonAsync(venues, new CreateVenueRequest("  "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await a.Organiser.PostAsJsonAsync(venues, new CreateVenueRequest("Zero", DefaultLimit: 0))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Organiser.PostAsJsonAsync(venues, new CreateVenueRequest("The Hall"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Organiser.PostAsJsonAsync(
            $"{venues}/{a.Venue.Id}/areas", new CreateVenueAreaRequest("Tiny", 0))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Organiser.PostAsJsonAsync(
            $"{venues}/{a.Venue.Id}/areas", new CreateVenueAreaRequest("Upstairs", 4))).StatusCode);
    }

    [Fact]
    public async Task Attaching_a_venue_sets_the_events_limit_and_location_and_toggling_an_area_changes_it()
    {
        var a = await ArrangeAsync();
        var ev = await CreateEventAsync(a.Guild);

        var attached = await SetVenueAsync(a, ev.Id, new SetEventVenueRequest(a.Venue.Id));
        Assert.Equal(10, attached.Effective.Limit);
        Assert.Equal(LimitSource.Venue, attached.Effective.Source);
        Assert.Equal("The Hall 10", attached.Effective.Explanation);
        var afterAttach = await GetEventAsync(ev.Id);
        Assert.Equal(10, afterAttach.AttendingOption!.Capacity);
        Assert.Equal("The Hall, 1 High Street", afterAttach.Location);

        var on = await ToggleAsync(a, ev.Id, a.Upstairs.Id, on: true);
        Assert.Equal(15, on.Effective.Limit);
        Assert.Equal("The Hall 10 + Upstairs 5 = 15", on.Effective.Explanation);
        Assert.Equal(15, (await GetEventAsync(ev.Id)).AttendingOption!.Capacity);

        var off = await ToggleAsync(a, ev.Id, a.Upstairs.Id, on: false);
        Assert.Equal(10, off.Effective.Limit);
        Assert.Equal(10, (await GetEventAsync(ev.Id)).AttendingOption!.Capacity);
    }

    [Fact]
    public async Task An_override_beats_the_venue_and_removing_it_returns_to_the_venue()
    {
        var a = await ArrangeAsync();
        var ev = await CreateEventAsync(a.Guild);
        await SetVenueAsync(a, ev.Id, new SetEventVenueRequest(a.Venue.Id));

        var overridden = await SetVenueAsync(a, ev.Id, new SetEventVenueRequest(a.Venue.Id, LimitOverride: 6));
        Assert.Equal(LimitSource.Override, overridden.Effective.Source);
        Assert.Equal(6, (await GetEventAsync(ev.Id)).AttendingOption!.Capacity);

        var cleared = await SetVenueAsync(a, ev.Id, new SetEventVenueRequest(a.Venue.Id));
        Assert.Equal(10, cleared.Effective.Limit);
        Assert.Equal(10, (await GetEventAsync(ev.Id)).AttendingOption!.Capacity);
    }

    [Fact]
    public async Task Raising_the_limit_promotes_the_waitlist_and_lowering_it_below_the_seated_is_refused()
    {
        var a = await ArrangeAsync(defaultLimit: 2);
        var ev = await CreateEventAsync(a.Guild);
        await SetVenueAsync(a, ev.Id, new SetEventVenueRequest(a.Venue.Id));
        var attending = (await GetEventAsync(ev.Id)).AttendingOption!.Id;
        foreach (var user in new long[] { 8001, 8002, 8003 })
        {
            (await Bot.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{user}", new RsvpRequest(attending))).EnsureSuccessStatusCode();
        }

        var before = await GetEventAsync(ev.Id);
        Assert.Contains(before.Rsvps, r => r.UserId == 8003 && r.Waitlisted);

        // Opening the upstairs adds 5 seats: the waitlisted guest is seated.
        await ToggleAsync(a, ev.Id, a.Upstairs.Id, on: true);
        var after = await GetEventAsync(ev.Id);
        Assert.DoesNotContain(after.Rsvps, r => r.Waitlisted);

        // Three are seated now; an override of 2 would unseat someone, so it is refused.
        var refused = await a.Organiser.PutAsJsonAsync(
            $"/guilds/{a.Guild}/game-nights/events/{ev.Id}/venue", new SetEventVenueRequest(a.Venue.Id, LimitOverride: 2));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        // ...and closing the area (back to 2) is refused the same way, leaving the limit as it was.
        var closing = await a.Organiser.PutAsJsonAsync(
            $"/guilds/{a.Guild}/game-nights/events/{ev.Id}/venue/areas/{a.Upstairs.Id}", new SetEventAreaRequest(false));
        Assert.Equal(HttpStatusCode.Conflict, closing.StatusCode);
        Assert.Equal(7, (await GetEventAsync(ev.Id)).AttendingOption!.Capacity);
    }

    [Fact]
    public async Task Editing_the_venue_default_reapplies_to_events_that_use_it()
    {
        var a = await ArrangeAsync();
        var ev = await CreateEventAsync(a.Guild);
        await SetVenueAsync(a, ev.Id, new SetEventVenueRequest(a.Venue.Id, AreaIdsOn: [a.Upstairs.Id]));
        Assert.Equal(15, (await GetEventAsync(ev.Id)).AttendingOption!.Capacity);

        var patched = await a.Organiser.PatchAsJsonAsync(
            $"/guilds/{a.Guild}/game-nights/venues/{a.Venue.Id}", new UpdateVenueRequest(DefaultLimit: 20));
        patched.EnsureSuccessStatusCode();
        Assert.Equal(25, (await GetEventAsync(ev.Id)).AttendingOption!.Capacity);

        var area = await a.Organiser.PatchAsJsonAsync(
            $"/guilds/{a.Guild}/game-nights/venues/{a.Venue.Id}/areas/{a.Upstairs.Id}", new UpdateVenueAreaRequest(ExtraCapacity: 10));
        area.EnsureSuccessStatusCode();
        Assert.Equal(30, (await GetEventAsync(ev.Id)).AttendingOption!.Capacity);

        // Retiring the area drops it from the sum.
        (await a.Organiser.PatchAsJsonAsync(
            $"/guilds/{a.Guild}/game-nights/venues/{a.Venue.Id}/areas/{a.Upstairs.Id}", new UpdateVenueAreaRequest(Active: false)))
            .EnsureSuccessStatusCode();
        Assert.Equal(20, (await GetEventAsync(ev.Id)).AttendingOption!.Capacity);
    }

    [Fact]
    public async Task A_venue_in_use_is_retired_not_deleted()
    {
        var a = await ArrangeAsync();
        var ev = await CreateEventAsync(a.Guild);
        await SetVenueAsync(a, ev.Id, new SetEventVenueRequest(a.Venue.Id, AreaIdsOn: [a.Upstairs.Id]));

        Assert.Equal(HttpStatusCode.Conflict,
            (await a.Organiser.DeleteAsync($"/guilds/{a.Guild}/game-nights/venues/{a.Venue.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await a.Organiser.DeleteAsync($"/guilds/{a.Guild}/game-nights/venues/{a.Venue.Id}/areas/{a.Upstairs.Id}")).StatusCode);

        (await a.Organiser.PatchAsJsonAsync(
            $"/guilds/{a.Guild}/game-nights/venues/{a.Venue.Id}", new UpdateVenueRequest(Active: false))).EnsureSuccessStatusCode();
        var active = await a.Member.GetFromJsonAsync<List<VenueDto>>($"/guilds/{a.Guild}/game-nights/venues");
        Assert.Empty(active!);
        var all = await a.Member.GetFromJsonAsync<List<VenueDto>>($"/guilds/{a.Guild}/game-nights/venues?includeRetired=true");
        Assert.Single(all!);

        // A venue nothing has used can be deleted.
        var fresh = await a.Organiser.PostAsJsonAsync($"/guilds/{a.Guild}/game-nights/venues", new CreateVenueRequest("Spare"));
        var spare = (await fresh.Content.ReadFromJsonAsync<VenueDto>())!;
        Assert.Equal(HttpStatusCode.NoContent,
            (await a.Organiser.DeleteAsync($"/guilds/{a.Guild}/game-nights/venues/{spare.Id}")).StatusCode);
    }

    [Fact]
    public async Task An_area_from_another_venue_or_an_event_from_another_server_is_rejected()
    {
        var a = await ArrangeAsync();
        var other = await ArrangeAsync();
        var ev = await CreateEventAsync(a.Guild);
        var foreignEvent = await CreateEventAsync(other.Guild);

        var wrongArea = await a.Organiser.PutAsJsonAsync($"/guilds/{a.Guild}/game-nights/events/{ev.Id}/venue",
            new SetEventVenueRequest(a.Venue.Id, AreaIdsOn: [other.Upstairs.Id]));
        Assert.Equal(HttpStatusCode.BadRequest, wrongArea.StatusCode);

        var wrongGuild = await a.Organiser.PutAsJsonAsync($"/guilds/{a.Guild}/game-nights/events/{foreignEvent.Id}/venue",
            new SetEventVenueRequest(a.Venue.Id));
        Assert.Equal(HttpStatusCode.NotFound, wrongGuild.StatusCode);

        var otherVenue = await a.Organiser.PutAsJsonAsync($"/guilds/{a.Guild}/game-nights/events/{ev.Id}/venue",
            new SetEventVenueRequest(other.Venue.Id));
        Assert.Equal(HttpStatusCode.BadRequest, otherVenue.StatusCode);
    }

    [Fact]
    public async Task A_restricted_event_is_not_found_for_a_member_who_is_not_invited()
    {
        var a = await ArrangeAsync();
        var ev = await CreateEventAsync(a.Guild);
        await SetVenueAsync(a, ev.Id, new SetEventVenueRequest(a.Venue.Id));
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var extension = await db.GameNightEventExtensions.FindAsync(ev.Id);
            extension!.Visibility = GameNightsVisibility.Restricted;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NotFound,
            (await a.Member.GetAsync($"/guilds/{a.Guild}/game-nights/events/{ev.Id}/venue")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await a.Organiser.GetAsync($"/guilds/{a.Guild}/game-nights/events/{ev.Id}/venue")).StatusCode);
    }

    [Fact]
    public async Task Detaching_the_venue_clears_its_areas_and_leaves_the_limit_alone()
    {
        var a = await ArrangeAsync();
        var ev = await CreateEventAsync(a.Guild);
        await SetVenueAsync(a, ev.Id, new SetEventVenueRequest(a.Venue.Id, AreaIdsOn: [a.Upstairs.Id]));

        var detached = await SetVenueAsync(a, ev.Id, new SetEventVenueRequest(null));

        Assert.Null(detached.Venue);
        Assert.Empty(detached.AreaIdsOn);
        Assert.Equal(LimitSource.None, detached.Effective.Source);
        Assert.Equal(15, (await GetEventAsync(ev.Id)).AttendingOption!.Capacity);
    }

    // ---- helpers ----

    private async Task<Arrangement> ArrangeAsync(int defaultLimit = 10)
    {
        var guild = NewGuildId();
        (await Bot.PutAsJsonAsync($"/guilds/{guild}/presence", new GuildPresenceRequest(true, "Venue test guild")))
            .EnsureSuccessStatusCode();
        // A server manager is Admin tier, which can act as an organiser.
        var (organiser, _) = await fixture.LoginAsync(guild * 1000 + 1, (guild, "G", true));
        var (member, _) = await fixture.LoginAsync(guild * 1000 + 2, (guild, "G", false));

        var created = await organiser.PostAsJsonAsync($"/guilds/{guild}/game-nights/venues",
            new CreateVenueRequest("The Hall", "1 High Street", defaultLimit));
        created.EnsureSuccessStatusCode();
        var venue = (await created.Content.ReadFromJsonAsync<VenueDto>())!;
        var areaResponse = await organiser.PostAsJsonAsync(
            $"/guilds/{guild}/game-nights/venues/{venue.Id}/areas", new CreateVenueAreaRequest("Upstairs", 5));
        areaResponse.EnsureSuccessStatusCode();
        var area = (await areaResponse.Content.ReadFromJsonAsync<VenueAreaDto>())!;
        return new Arrangement(guild, organiser, member, venue, area);
    }

    private async Task<EventDto> CreateEventAsync(long guild)
    {
        var response = await Bot.PostAsJsonAsync($"/guilds/{guild}/events",
            new CreateEventRequest(9000, "Game night", "in 3 hours", 54000 + guild % 1000));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task<EventDto> GetEventAsync(Guid id) =>
        (await Bot.GetFromJsonAsync<EventDto>($"/events/{id}"))!;

    private static async Task<EventVenueDto> SetVenueAsync(Arrangement a, Guid eventId, SetEventVenueRequest request)
    {
        var response = await a.Organiser.PutAsJsonAsync($"/guilds/{a.Guild}/game-nights/events/{eventId}/venue", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventVenueDto>())!;
    }

    private static async Task<EventVenueDto> ToggleAsync(Arrangement a, Guid eventId, Guid areaId, bool on)
    {
        var response = await a.Organiser.PutAsJsonAsync(
            $"/guilds/{a.Guild}/game-nights/events/{eventId}/venue/areas/{areaId}", new SetEventAreaRequest(on));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventVenueDto>())!;
    }
}
