using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests.GameNights;

/// <summary>Reminders switched off.</summary>
public sealed class GameNightsNoReminderFixture : WebAuthFixture
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("GameNights:Enabled", "true");
        builder.UseSetting("GameNights:ResultReminderHours", "0");
    }
}

/// <summary>Step 8 on the API side: everything the bot says about a result goes through the outbox, queued with the result;
/// the per-game photo setting; and the message the bot records for the result post.</summary>
public class GameNightsReportFlowApiTests(GameNightsFixture fixture) : IClassFixture<GameNightsFixture>
{
    private HttpClient Bot => fixture.Client;

    private static long nextGuild = 66000;

    private static long NewGuildId() => Interlocked.Increment(ref nextGuild);

    private sealed record Night(long Guild, HttpClient Admin, long A, HttpClient AClient, long B, HttpClient BClient, GameDto Arcs, TableDto Table);

    private static string Base(Night n) => $"/guilds/{n.Guild}/game-nights";

    private static SubmittedPlayer P(long id, int place, string colour) =>
        new(id, null, place, new Dictionary<string, string> { ["colour"] = colour, ["score"] = (10 - place).ToString() });

    private static ResultSubmission TwoPlayer(Night n) => new([P(n.A, 1, "Red"), P(n.B, 2, "Blue")]);

    [Fact]
    public async Task Reporting_queues_the_post_for_the_thread_now_and_one_reminder_a_day_later()
    {
        var n = await ArrangeAsync();

        var result = await SubmitAsync(n, n.AClient);

        var submitted = Assert.Single(await DeliveriesAsync(DeliveryType.ResultSubmitted, result.Id));
        var reminder = Assert.Single(await DeliveriesAsync(DeliveryType.ResultReminder, result.Id));
        Assert.True(submitted.DueAt - submitted.CreatedAt < NodaTime.Duration.FromSeconds(1));
        Assert.InRange((reminder.DueAt - reminder.CreatedAt).TotalHours, 23.9, 24.1);
        var payload = JsonSerializer.Deserialize<ResultNoticePayload>(submitted.PayloadJson)!;
        Assert.Equal((result.Id, n.Guild, n.Table.Id), (payload.ResultId, payload.GuildId, payload.TableId));
        Assert.Equal(submitted.ChannelId, reminder.ChannelId); // the night's channel: the bot finds the thread from the table
    }

    [Fact]
    public async Task A_result_confirmed_on_the_spot_has_a_post_but_no_reminder()
    {
        var n = await ArrangeAsync();
        var game = new ResultSubmission([P(n.A, 1, "Red"), new SubmittedPlayer(null, await GuestAsync(n), 2, new Dictionary<string, string> { ["colour"] = "Blue", ["score"] = "3" })]);

        var response = await n.AClient.PostAsJsonAsync($"{Base(n)}/tables/{n.Table.Id}/results", new SubmitResultRequest(game));
        var result = (await response.Content.ReadFromJsonAsync<ResultDto>())!;

        Assert.Equal(ResultStatus.Confirmed, result.Status);
        Assert.Single(await DeliveriesAsync(DeliveryType.ResultSubmitted, result.Id));
        Assert.Empty(await DeliveriesAsync(DeliveryType.ResultReminder, result.Id));
    }

    [Fact]
    public async Task The_last_confirmation_queues_one_settled_notice_and_a_moderators_void_queues_another()
    {
        var n = await ArrangeAsync();
        var result = await SubmitAsync(n, n.AClient);
        Assert.Empty(await DeliveriesAsync(DeliveryType.ResultSettled, result.Id)); // nothing to announce yet

        (await n.BClient.PostAsJsonAsync($"{Base(n)}/results/{result.Id}/confirm", new AnswerResultRequest())).EnsureSuccessStatusCode();
        Assert.Single(await DeliveriesAsync(DeliveryType.ResultSettled, result.Id));

        // Confirming again changes nothing and announces nothing.
        await n.BClient.PostAsJsonAsync($"{Base(n)}/results/{result.Id}/confirm", new AnswerResultRequest());
        Assert.Single(await DeliveriesAsync(DeliveryType.ResultSettled, result.Id));

        (await n.Admin.PostAsJsonAsync($"{Base(n)}/results/{result.Id}/resolve", new ResolveResultRequest(ResolveAction.Void))).EnsureSuccessStatusCode();
        Assert.Equal(2, (await DeliveriesAsync(DeliveryType.ResultSettled, result.Id)).Count);
        await n.Admin.PostAsJsonAsync($"{Base(n)}/results/{result.Id}/resolve", new ResolveResultRequest(ResolveAction.Void));
        Assert.Equal(2, (await DeliveriesAsync(DeliveryType.ResultSettled, result.Id)).Count); // already withdrawn
    }

    [Fact]
    public async Task A_rejection_does_not_queue_a_settled_notice_but_a_moderators_confirmation_does()
    {
        var n = await ArrangeAsync();
        var result = await SubmitAsync(n, n.AClient);

        (await n.BClient.PostAsJsonAsync($"{Base(n)}/results/{result.Id}/reject", new AnswerResultRequest())).EnsureSuccessStatusCode();
        Assert.Empty(await DeliveriesAsync(DeliveryType.ResultSettled, result.Id));

        (await n.Admin.PostAsJsonAsync($"{Base(n)}/results/{result.Id}/resolve", new ResolveResultRequest(ResolveAction.Confirm))).EnsureSuccessStatusCode();
        Assert.Single(await DeliveriesAsync(DeliveryType.ResultSettled, result.Id));
    }

    [Fact]
    public async Task The_bot_records_the_post_it_made_and_only_the_bot_may()
    {
        var n = await ArrangeAsync();
        var result = await SubmitAsync(n, n.AClient);
        Assert.Null(result.SummaryMessageId);
        var url = $"{Base(n)}/results/{result.Id}/summary-message";

        Assert.Equal(HttpStatusCode.Forbidden, (await n.Admin.PutAsJsonAsync(url, new SetResultMessageRequest(5))).StatusCode);
        var set = await Bot.PutAsJsonAsync(url, new SetResultMessageRequest(987654321));

        Assert.Equal(987654321L, (await set.Content.ReadFromJsonAsync<ResultDto>())!.SummaryMessageId);
        Assert.Equal(987654321L, (await n.AClient.GetFromJsonAsync<ResultDto>($"{Base(n)}/results/{result.Id}"))!.SummaryMessageId);
    }

    [Fact]
    public async Task The_photo_setting_is_a_yes_or_no_on_the_game_that_organisers_and_the_seed_can_carry()
    {
        var n = await ArrangeAsync();
        Assert.False(n.Arcs.Spec.PhotoRequired);

        var changed = await n.Admin.PutAsJsonAsync($"{Base(n)}/games/{n.Arcs.Id}", n.Arcs.Spec with { PhotoRequired = true });
        changed.EnsureSuccessStatusCode();

        Assert.True((await changed.Content.ReadFromJsonAsync<GameDto>())!.Spec.PhotoRequired);
        Assert.True((await n.AClient.GetFromJsonAsync<GameDto>($"{Base(n)}/games/{n.Arcs.Id}"))!.Spec.PhotoRequired);
    }

    [Fact]
    public async Task A_result_submitted_with_the_photo_setting_on_is_not_refused_by_the_api_which_never_sees_a_photo()
    {
        var n = await ArrangeAsync();
        (await n.Admin.PutAsJsonAsync($"{Base(n)}/games/{n.Arcs.Id}", n.Arcs.Spec with { PhotoRequired = true })).EnsureSuccessStatusCode();

        // The flow in Discord blocks until the reporter says the photo is posted; the API only stores the setting.
        var result = await SubmitAsync(n, n.AClient);

        Assert.Equal(ResultStatus.Pending, result.Status);
    }

    // ---- helpers ----

    private async Task<Night> ArrangeAsync()
    {
        var guild = NewGuildId();
        (await Bot.PutAsJsonAsync($"/guilds/{guild}/presence", new GuildPresenceRequest(true, "Report flow guild"))).EnsureSuccessStatusCode();
        var (admin, _) = await fixture.LoginAsync(guild * 1000 + 1, (guild, "G", true));
        var (a, sessionA) = await fixture.LoginAsync(guild * 1000 + 2, (guild, "G", false));
        var (b, sessionB) = await fixture.LoginAsync(guild * 1000 + 3, (guild, "G", false));
        (await admin.PostAsync($"/guilds/{guild}/game-nights/games/seed", null)).EnsureSuccessStatusCode();
        var arcs = (await admin.GetFromJsonAsync<List<GameDto>>($"/guilds/{guild}/game-nights/games"))!.Single(g => g.Spec.Key == "arcs");
        var ev = await Bot.PostAsJsonAsync($"/guilds/{guild}/events", new CreateEventRequest(9000, "Game night", "in 3 hours", 67000 + guild % 1000));
        var eventId = (await ev.Content.ReadFromJsonAsync<EventDto>())!.Id;
        var table = (await (await admin.PostAsJsonAsync($"/guilds/{guild}/game-nights/events/{eventId}/tables", new CreateTableRequest(arcs.Id)))
            .Content.ReadFromJsonAsync<TableDto>())!;
        return new Night(guild, admin, sessionA.UserId, a, sessionB.UserId, b, arcs, table);
    }

    private async Task<Guid> GuestAsync(Night n) =>
        (await (await n.Admin.PostAsJsonAsync($"{Base(n)}/guests", new CreateGuestRequest("Sam T"))).Content.ReadFromJsonAsync<GuestDto>())!.Id;

    private async Task<ResultDto> SubmitAsync(Night n, HttpClient by)
    {
        var response = await by.PostAsJsonAsync($"{Base(n)}/tables/{n.Table.Id}/results", new SubmitResultRequest(TwoPlayer(n)));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ResultDto>())!;
    }

    private async Task<List<Delivery>> DeliveriesAsync(DeliveryType type, Guid resultId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var all = await db.Deliveries.AsNoTracking().Where(d => d.Type == type).OrderBy(d => d.CreatedAt).ToListAsync();
        return [.. all.Where(d => d.PayloadJson.Contains(resultId.ToString(), StringComparison.OrdinalIgnoreCase))];
    }
}

/// <summary>With reminders off (hours = 0) a pending result queues only its post.</summary>
public class GameNightsNoReminderTests(GameNightsNoReminderFixture fixture) : IClassFixture<GameNightsNoReminderFixture>
{
    [Fact]
    public async Task No_reminder_is_queued_when_reminders_are_switched_off()
    {
        var guild = 68001L;
        await fixture.Client.PutAsJsonAsync($"/guilds/{guild}/presence", new GuildPresenceRequest(true, "No reminder guild"));
        var (admin, _) = await fixture.LoginAsync(guild * 1000 + 1, (guild, "G", true));
        var a = await fixture.LoginAsync(guild * 1000 + 2, (guild, "G", false));
        var b = await fixture.LoginAsync(guild * 1000 + 3, (guild, "G", false));
        await admin.PostAsync($"/guilds/{guild}/game-nights/games/seed", null);
        var arcs = (await admin.GetFromJsonAsync<List<GameDto>>($"/guilds/{guild}/game-nights/games"))!.Single(g => g.Spec.Key == "arcs");
        var ev = await fixture.Client.PostAsJsonAsync($"/guilds/{guild}/events", new CreateEventRequest(9000, "Night", "in 3 hours", 68100));
        var eventId = (await ev.Content.ReadFromJsonAsync<EventDto>())!.Id;
        var table = (await (await admin.PostAsJsonAsync($"/guilds/{guild}/game-nights/events/{eventId}/tables", new CreateTableRequest(arcs.Id)))
            .Content.ReadFromJsonAsync<TableDto>())!;
        var report = new ResultSubmission(
        [
            new(a.Session.UserId, null, 1, new Dictionary<string, string> { ["colour"] = "Red", ["score"] = "9" }),
            new(b.Session.UserId, null, 2, new Dictionary<string, string> { ["colour"] = "Blue", ["score"] = "8" }),
        ]);

        var created = await a.Client.PostAsJsonAsync($"/guilds/{guild}/game-nights/tables/{table.Id}/results", new SubmitResultRequest(report));
        var result = (await created.Content.ReadFromJsonAsync<ResultDto>())!;

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var types = (await db.Deliveries.AsNoTracking().ToListAsync())
            .Where(d => d.PayloadJson.Contains(result.Id.ToString(), StringComparison.OrdinalIgnoreCase)).Select(d => d.Type).ToList();
        Assert.Equal([DeliveryType.ResultSubmitted], types);
    }
}
