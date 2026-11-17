using System.Net.Http.Json;
using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests.GameNights;

/// <summary>Step 11's load check: a few hundred people answering a night at the same moment must leave every answer stored
/// and produce ONE held-back edit of the event post, not one each. Sequential tests cannot catch the race this guards
/// against (two requests both seeing "no edit waiting" and both queueing one), so these send the answers concurrently.</summary>
public class GameNightsLoadApiTests(GameNightsFixture fixture) : IClassFixture<GameNightsFixture>
{
    private const int People = 300;
    private const int AtOnce = 40;

    private static long nextGuild = 72000;

    private HttpClient Bot => fixture.Client;

    private sealed record Night(long Guild, GameDto Root, GameDto Arcs, Guid EventId);

    [Fact]
    public async Task Three_hundred_answers_at_once_are_all_kept_and_queue_a_single_edit_of_the_event_post()
    {
        var n = await ArrangeAsync();
        var url = $"/guilds/{n.Guild}/game-nights/events/{n.EventId}/signals";

        await AnswerAllAsync(n, url, firstPerson: 0);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        Assert.Equal(People, await db.GameNightSignals.CountAsync(s => s.EventId == n.EventId));
        Assert.Equal(People, await db.GameNightSignals.Where(s => s.EventId == n.EventId).Select(s => s.UserId).Distinct().CountAsync());

        var queued = await PendingSyncsAsync(db, n.EventId);
        var only = Assert.Single(queued); // 300 answers, one edit
        Assert.True(only.DueAt - only.CreatedAt >= NodaTime.Duration.FromSeconds(15), "the edit is held back by the interval, not sent at once");

        var summary = await Bot.GetFromJsonAsync<NightSignalsSummary>(url);
        Assert.Equal(People, summary!.Respondents);
    }

    [Fact]
    public async Task A_second_wave_after_the_edit_is_picked_up_queues_exactly_one_more()
    {
        var n = await ArrangeAsync();
        var url = $"/guilds/{n.Guild}/game-nights/events/{n.EventId}/signals";
        await AnswerAllAsync(n, url, firstPerson: 0, count: 120);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            // The bot has been served the waiting row: it may have read the data before the next answers.
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            foreach (var d in await PendingSyncsAsync(db, n.EventId, track: true))
            {
                d.Attempts = 1;
            }

            await db.SaveChangesAsync();
        }

        await AnswerAllAsync(n, url, firstPerson: 1000, count: 120);

        await using var check = fixture.Factory.Services.CreateAsyncScope();
        var all = await PendingSyncsAsync(check.ServiceProvider.GetRequiredService<CalCronyDbContext>(), n.EventId);
        Assert.Equal(2, all.Count); // the one the bot picked up, and one for everything since
    }

    // ---- helpers ----

    private async Task AnswerAllAsync(Night n, string url, int firstPerson, int count = People)
    {
        using var gate = new SemaphoreSlim(AtOnce);
        var tasks = Enumerable.Range(firstPerson, count).Select(async i =>
        {
            await gate.WaitAsync();
            try
            {
                var game = i % 2 == 0 ? n.Root.Id : n.Arcs.Id;
                var response = await Bot.PutAsJsonAsync($"{url}/{game}", new SetSignalRequest((SignalKind)(i % 4), UserId: 800000 + i));
                response.EnsureSuccessStatusCode();
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task<Night> ArrangeAsync()
    {
        var guild = Interlocked.Increment(ref nextGuild);
        (await Bot.PutAsJsonAsync($"/guilds/{guild}/presence", new GuildPresenceRequest(true, "Load test guild"))).EnsureSuccessStatusCode();
        var (admin, _) = await fixture.LoginAsync(guild * 1000 + 1, (guild, "G", true));
        (await admin.PostAsync($"/guilds/{guild}/game-nights/games/seed", null)).EnsureSuccessStatusCode();
        var games = (await admin.GetFromJsonAsync<List<GameDto>>($"/guilds/{guild}/game-nights/games"))!;
        var ev = await Bot.PostAsJsonAsync($"/guilds/{guild}/events", new CreateEventRequest(9000, "Load night", "in 3 hours", 72000 + guild % 1000));
        ev.EnsureSuccessStatusCode();
        var eventId = (await ev.Content.ReadFromJsonAsync<EventDto>())!.Id;
        (await Bot.PutAsJsonAsync($"/events/{eventId}/message", new SetEventMessageRequest(72000 + guild % 1000, 575000 + guild, "general"))).EnsureSuccessStatusCode();
        return new Night(guild, games.Single(g => g.Spec.Key == "root"), games.Single(g => g.Spec.Key == "arcs"), eventId);
    }

    private static async Task<List<Delivery>> PendingSyncsAsync(CalCronyDbContext db, Guid eventId, bool track = false)
    {
        var query = track ? db.Deliveries : db.Deliveries.AsNoTracking();
        var all = await query.Where(d => d.Type == DeliveryType.SyncEventMessage && d.Status == DeliveryStatus.Pending).ToListAsync();
        return [.. all.Where(d => JsonSerializer.Deserialize<SyncEventMessagePayload>(d.PayloadJson)!.EventId == eventId)];
    }
}
