using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests;

/// <summary>Fork (event import): the bot-only extras on event creation. A script holding the API
/// key creates the event, has the API queue the embed post, seats the attendees before the first
/// render and adopts an existing Discord scheduled event.</summary>
public class EventImportApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 9800;
    private const long ChannelId = 9801;
    private const long NativeId = 777_000_111;

    [Fact]
    public async Task Bot_import_queues_the_post_seats_attendees_and_adopts_the_native_event()
    {
        await SeedGuildAsync();

        var response = await fixture.Client.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(9810, "Imported night", "in 3 days at 6pm", ChannelId,
                PostViaOutbox: true, InitialAttendeeIds: [9821, 9822, 9821], NativeEventId: NativeId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ev = (await response.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.Equal(9810, ev.CreatorId);
        Assert.Equal(NativeId, ev.NativeEventId);
        Assert.Equal(1, await CountDeliveriesAsync(DeliveryType.PostEventMessage, ev.Id));

        var attending = ev.Options.Single(o => o.IsAttending);
        Assert.Equal([9821L, 9822L], ev.Rsvps.Select(r => r.UserId).Order());
        Assert.All(ev.Rsvps, r => Assert.Equal(attending.Id, r.OptionId));
        Assert.All(ev.Rsvps, r => Assert.False(r.Waitlisted));
    }

    [Fact]
    public async Task Plain_bot_create_is_unchanged_it_posts_nothing_itself()
    {
        await SeedGuildAsync();

        var response = await fixture.Client.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(9810, "Slash command night", "in 3 days at 6pm", ChannelId));

        var ev = (await response.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.Equal(0, await CountDeliveriesAsync(DeliveryType.PostEventMessage, ev.Id));
        Assert.Empty(ev.Rsvps);
        Assert.Null(ev.NativeEventId);
    }

    [Fact]
    public async Task Attendees_past_the_limit_are_waitlisted_in_order()
    {
        await SeedGuildAsync();

        var response = await fixture.Client.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(9810, "Small table", "in 3 days at 6pm", ChannelId,
                AttendeeLimit: 2, PostViaOutbox: true, InitialAttendeeIds: [9831, 9832, 9833]));

        var ev = (await response.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.Equal(2, ev.Rsvps.Count(r => !r.Waitlisted));
        var waitlisted = Assert.Single(ev.Rsvps, r => r.Waitlisted);
        Assert.Equal(9833, waitlisted.UserId);
    }

    [Fact]
    public async Task Too_many_attendees_is_a_clear_400()
    {
        await SeedGuildAsync();

        var response = await fixture.Client.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(9810, "Huge", "in 3 days at 6pm", ChannelId,
                InitialAttendeeIds: [.. Enumerable.Range(1, 501).Select(i => (long)i)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_repeating_import_seats_only_the_first_occurrence()
    {
        await SeedGuildAsync();

        var response = await fixture.Client.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(9810, "Fortnightly", "in 3 days at 6pm", ChannelId,
                Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week, 2),
                PostViaOutbox: true, InitialAttendeeIds: [9841], NativeEventId: NativeId + 1));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ev = (await response.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.NotNull(ev.SeriesId);
        Assert.Single(ev.Rsvps);
        Assert.Equal(NativeId + 1, ev.NativeEventId);
    }

    [Fact]
    public async Task Web_callers_cannot_use_any_of_it()
    {
        await SeedGuildAsync();
        var (member, session) = await fixture.LoginAsync(9850, (GuildId, "G", false));

        var response = await member.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(0, "Web night", "in 3 days at 6pm", 0,
                PostViaOutbox: true, InitialAttendeeIds: [9851, 9852], NativeEventId: NativeId + 2));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ev = (await response.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.Equal(session.UserId, ev.CreatorId);
        Assert.Empty(ev.Rsvps);
        Assert.Null(ev.NativeEventId);
        // The web's own post is still queued exactly once, not doubled by the flag.
        Assert.Equal(1, await CountDeliveriesAsync(DeliveryType.PostEventMessage, ev.Id));
    }

    private async Task SeedGuildAsync()
    {
        var response = await fixture.Client.PutAsJsonAsync($"/guilds/{GuildId}/settings",
            new GuildSettingsDto("UTC", ChannelId));
        response.EnsureSuccessStatusCode();
    }

    private async Task<int> CountDeliveriesAsync(DeliveryType type, Guid eventId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var needle = eventId.ToString();
        return await db.Deliveries.CountAsync(d => d.Type == type && d.PayloadJson.Contains(needle));
    }
}
