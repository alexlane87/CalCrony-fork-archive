using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Api.GameNights;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests.GameNights;

/// <summary>The arithmetic (no database).</summary>
public class SignalMathTests
{
    private static readonly Guid Root = Guid.NewGuid();
    private static readonly Guid Arcs = Guid.NewGuid();
    private static readonly Guid Go = Guid.NewGuid();
    private static readonly (Guid, string)[] Games = [(Root, "Root"), (Arcs, "Arcs"), (Go, "Go")];

    private static PersonSignalDto S(long user, Guid game, SignalKind kind) => new(user, game, kind);

    [Fact]
    public void Demand_counts_each_answer_per_game_and_ranks_the_busiest_first()
    {
        var summary = SignalMath.Summarize(Games,
        [
            S(1, Root, SignalKind.CanTeach), S(2, Root, SignalKind.CanPlay), S(3, Root, SignalKind.NewToIt), S(4, Root, SignalKind.NotTonight),
            S(2, Arcs, SignalKind.NewToIt),
        ]);

        Assert.Equal(["Root", "Arcs", "Go"], summary.Games.Select(g => g.GameName));
        var root = summary.Games[0];
        Assert.Equal((1, 1, 1, 1, 3), (root.CanTeach, root.CanPlay, root.NewToIt, root.NotTonight, root.Interested));
        Assert.True(root.HasTeacher);
        Assert.Equal(0, summary.Games[2].Interested); // every active game appears, even with no answers
    }

    [Fact]
    public void A_game_people_want_but_nobody_can_teach_is_flagged_and_passing_on_it_is_not_demand()
    {
        var summary = SignalMath.Summarize(Games, [S(1, Arcs, SignalKind.NewToIt), S(2, Arcs, SignalKind.CanPlay), S(3, Go, SignalKind.NotTonight)]);

        Assert.True(summary.Games.Single(g => g.GameName == "Arcs").NeedsTeacher);
        Assert.False(summary.Games.Single(g => g.GameName == "Go").NeedsTeacher); // only "not tonight"
        Assert.False(summary.Games.Single(g => g.GameName == "Root").NeedsTeacher); // nobody wants it
    }

    [Fact]
    public void People_counts_are_distinct_people_not_answers()
    {
        var summary = SignalMath.Summarize(Games,
        [
            S(1, Root, SignalKind.CanTeach), S(1, Arcs, SignalKind.CanTeach), // one teacher, two games
            S(2, Root, SignalKind.CanPlay), S(2, Go, SignalKind.NewToIt),     // experienced AND fresh
            S(3, Arcs, SignalKind.NewToIt),
            S(4, Root, SignalKind.NotTonight),
        ]);

        Assert.Equal((1, 2, 2, 4), (summary.Teachers, summary.Experienced, summary.Fresh, summary.Respondents));
    }

    [Fact]
    public void Answers_about_games_that_are_not_active_are_ignored_and_an_empty_night_is_all_zeros()
    {
        var summary = SignalMath.Summarize(Games, [S(1, Guid.NewGuid(), SignalKind.CanTeach)]);

        Assert.Equal((0, 0, 0, 0), (summary.Teachers, summary.Experienced, summary.Fresh, summary.Respondents));
        Assert.All(summary.Games, g => Assert.Equal(0, g.Interested));
    }

    [Fact]
    public void The_roster_lists_teachers_per_game_that_has_any_in_name_order()
    {
        var roster = SignalMath.Roster(Games,
            [S(9, Root, SignalKind.CanTeach), S(3, Root, SignalKind.CanTeach), S(3, Root, SignalKind.CanTeach), S(5, Arcs, SignalKind.CanTeach), S(6, Go, SignalKind.CanPlay)]);

        Assert.Equal(["Arcs", "Root"], roster.Select(r => r.GameName));
        Assert.Equal([3L, 9L], roster.Single(r => r.GameName == "Root").Teachers);
    }
}

/// <summary>Step 6 over HTTP: answering, replacing, the roster and people lists, the visibility of both, and the
/// throttle on the event post. A web manager is Admin tier; a plain web member answers for themselves.</summary>
public class GameNightsSignalApiTests(GameNightsFixture fixture) : IClassFixture<GameNightsFixture>
{
    private HttpClient Bot => fixture.Client;

    private static long nextGuild = 60000;

    private static long NewGuildId() => Interlocked.Increment(ref nextGuild);

    private sealed record Night(long Guild, HttpClient Admin, HttpClient Member, long MemberId, GameDto Root, GameDto Arcs, Guid EventId);

    private static string Url(Night n, string tail = "") => $"/guilds/{n.Guild}/game-nights/events/{n.EventId}/signals{tail}";

    [Fact]
    public async Task A_member_answers_per_game_and_changing_an_answer_replaces_the_old_one()
    {
        var n = await ArrangeAsync();

        await SetAsync(n.Member, Url(n, $"/{n.Root.Id}"), SignalKind.NewToIt);
        var changed = await SetAsync(n.Member, Url(n, $"/{n.Root.Id}"), SignalKind.CanTeach);

        var only = Assert.Single(changed.Signals);
        Assert.Equal((n.Root.Id, SignalKind.CanTeach), (only.GameId, only.Kind));
        var summary = await n.Member.GetFromJsonAsync<NightSignalsSummary>(Url(n));
        var root = summary!.Games.Single(g => g.GameId == n.Root.Id);
        Assert.Equal((1, 0), (root.CanTeach, root.NewToIt)); // the new-to-it answer is gone, not added to

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        Assert.Equal(1, await db.GameNightSignals.CountAsync(s => s.EventId == n.EventId && s.UserId == n.MemberId));
    }

    [Fact]
    public async Task Clearing_an_answer_removes_it_and_the_bulk_form_replaces_everything()
    {
        var n = await ArrangeAsync();
        await SetAsync(n.Member, Url(n, $"/{n.Root.Id}"), SignalKind.CanPlay);
        await SetAsync(n.Member, Url(n, $"/{n.Arcs.Id}"), SignalKind.CanPlay);

        var cleared = await n.Member.PutAsJsonAsync(Url(n, $"/{n.Root.Id}"), new SetSignalRequest(null));
        Assert.Equal([n.Arcs.Id], (await cleared.Content.ReadFromJsonAsync<MySignalsDto>())!.Signals.Select(s => s.GameId));

        var replaced = await n.Member.PutAsJsonAsync(Url(n, "/mine"),
            new SetMySignalsRequest([new(n.Root.Id, SignalKind.NotTonight)]));
        var now = (await replaced.Content.ReadFromJsonAsync<MySignalsDto>())!;
        Assert.Equal([(n.Root.Id, SignalKind.NotTonight)], now.Signals.Select(s => (s.GameId, s.Kind)));
        Assert.Equal(now.Signals, (await n.Member.GetFromJsonAsync<MySignalsDto>(Url(n, "/mine")))!.Signals);
    }

    [Fact]
    public async Task The_bot_answers_for_a_named_member_and_must_name_one()
    {
        var n = await ArrangeAsync();

        var noOne = await Bot.PutAsJsonAsync(Url(n, $"/{n.Root.Id}"), new SetSignalRequest(SignalKind.CanPlay));
        Assert.Equal(HttpStatusCode.BadRequest, noOne.StatusCode);

        (await Bot.PutAsJsonAsync(Url(n, $"/{n.Root.Id}"), new SetSignalRequest(SignalKind.CanTeach, UserId: 4242))).EnsureSuccessStatusCode();
        var theirs = await Bot.GetFromJsonAsync<MySignalsDto>(Url(n, "/mine?userId=4242"));
        Assert.Equal(SignalKind.CanTeach, Assert.Single(theirs!.Signals).Kind);
    }

    [Fact]
    public async Task The_roster_names_teachers_and_the_people_list_is_for_organisers()
    {
        var n = await ArrangeAsync();
        await SetAsync(n.Member, Url(n, $"/{n.Root.Id}"), SignalKind.CanTeach);
        await Bot.PutAsJsonAsync(Url(n, $"/{n.Arcs.Id}"), new SetSignalRequest(SignalKind.NewToIt, UserId: 77));

        var roster = await n.Member.GetFromJsonAsync<List<TeacherRosterEntryDto>>(Url(n, "/roster"));
        var entry = Assert.Single(roster!);
        Assert.Equal("Root", entry.GameName);
        Assert.Equal([n.MemberId], entry.Teachers);

        Assert.Equal(HttpStatusCode.Forbidden, (await n.Member.GetAsync(Url(n, "/people"))).StatusCode);
        var people = await n.Admin.GetFromJsonAsync<List<PersonSignalDto>>(Url(n, "/people"));
        Assert.Equal(2, people!.Count);
        Assert.Contains(people, p => p.UserId == 77 && p.Kind == SignalKind.NewToIt);
    }

    [Fact]
    public async Task Bad_answers_retired_games_and_other_servers_games_are_refused()
    {
        var n = await ArrangeAsync();
        var other = await ArrangeAsync();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await n.Member.PutAsJsonAsync(Url(n, $"/{n.Root.Id}"), new SetSignalRequest((SignalKind)9))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await n.Member.PutAsJsonAsync(Url(n, $"/{other.Root.Id}"), new SetSignalRequest(SignalKind.CanPlay))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.Member.GetAsync(Url(n))).StatusCode); // a night in another server

        (await n.Admin.PostAsync($"/guilds/{n.Guild}/game-nights/games/{n.Arcs.Id}/retire", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await n.Member.PutAsJsonAsync(Url(n, $"/{n.Arcs.Id}"), new SetSignalRequest(SignalKind.CanPlay))).StatusCode);
        var summary = await n.Member.GetFromJsonAsync<NightSignalsSummary>(Url(n));
        Assert.DoesNotContain(summary!.Games, g => g.GameId == n.Arcs.Id);
    }

    [Fact]
    public async Task A_hidden_night_has_no_answers_to_see_or_give()
    {
        var n = await ArrangeAsync();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            db.GameNightEventExtensions.Add(new GameNightEventExtension { EventId = n.EventId, Visibility = GameNightsVisibility.InviteOnly });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NotFound, (await n.Member.GetAsync(Url(n))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await n.Member.PutAsJsonAsync(Url(n, $"/{n.Root.Id}"), new SetSignalRequest(SignalKind.CanPlay))).StatusCode);
    }

    [Fact]
    public async Task The_event_the_bot_renders_the_post_from_carries_the_nights_summary_when_the_server_has_games()
    {
        var n = await ArrangeAsync();
        await SetAsync(n.Member, Url(n, $"/{n.Root.Id}"), SignalKind.CanTeach);

        var ev = await Bot.GetFromJsonAsync<EventDto>($"/events/{n.EventId}");

        Assert.NotNull(ev!.GameNights);
        Assert.Equal((1, 1), (ev.GameNights!.Teachers, ev.GameNights.Respondents));
        Assert.Equal(2, ev.GameNights.Games.Count);
    }

    [Fact]
    public async Task A_server_with_no_games_gets_an_event_with_no_summary_and_the_post_is_unchanged()
    {
        var guild = NewGuildId();
        var created = await Bot.PostAsJsonAsync($"/guilds/{guild}/events", new CreateEventRequest(9000, "Plain night", "in 3 hours", 61000 + guild % 1000));
        var id = (await created.Content.ReadFromJsonAsync<EventDto>())!.Id;

        Assert.Null((await Bot.GetFromJsonAsync<EventDto>($"/events/{id}"))!.GameNights);
    }

    [Fact]
    public async Task Many_answers_in_a_window_queue_exactly_one_delayed_edit_of_the_post()
    {
        var n = await ArrangeAsync(withPost: true);

        // Twenty changes from several people, as fast as the test can send them.
        for (var i = 0; i < 20; i++)
        {
            await Bot.PutAsJsonAsync(Url(n, $"/{(i % 2 == 0 ? n.Root.Id : n.Arcs.Id)}"),
                new SetSignalRequest((SignalKind)(i % 4), UserId: 1000 + i));
        }

        var queued = await PendingSyncsAsync(n.EventId);
        var only = Assert.Single(queued);
        // Held back by the interval (20 s by default), not sent at once.
        Assert.True(only.DueAt - only.CreatedAt >= NodaTime.Duration.FromSeconds(15), $"due {only.DueAt - only.CreatedAt} after it was queued");
    }

    [Fact]
    public async Task Once_the_edit_has_been_picked_up_the_next_change_queues_the_next_one()
    {
        var n = await ArrangeAsync(withPost: true);
        await Bot.PutAsJsonAsync(Url(n, $"/{n.Root.Id}"), new SetSignalRequest(SignalKind.CanPlay, UserId: 1));
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            // The bot has been served the row (an attempt was made): it may have read the data before the next change.
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            foreach (var d in await db.Deliveries.Where(d => d.Type == DeliveryType.SyncEventMessage).ToListAsync())
            {
                if (d.PayloadJson.Contains(n.EventId.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    d.Attempts = 1;
                }
            }

            await db.SaveChangesAsync();
        }

        await Bot.PutAsJsonAsync(Url(n, $"/{n.Root.Id}"), new SetSignalRequest(SignalKind.CanTeach, UserId: 2));

        Assert.Equal(2, (await PendingSyncsAsync(n.EventId)).Count);
    }

    [Fact]
    public async Task A_night_with_no_post_queues_nothing()
    {
        var n = await ArrangeAsync(withPost: false);

        await Bot.PutAsJsonAsync(Url(n, $"/{n.Root.Id}"), new SetSignalRequest(SignalKind.CanPlay, UserId: 1));

        Assert.Empty(await PendingSyncsAsync(n.EventId));
    }

    // ---- helpers ----

    private async Task<Night> ArrangeAsync(bool withPost = false)
    {
        var guild = NewGuildId();
        (await Bot.PutAsJsonAsync($"/guilds/{guild}/presence", new GuildPresenceRequest(true, "Signals test guild"))).EnsureSuccessStatusCode();
        var (admin, _) = await fixture.LoginAsync(guild * 1000 + 1, (guild, "G", true));
        var (member, session) = await fixture.LoginAsync(guild * 1000 + 2, (guild, "G", false));
        (await admin.PostAsync($"/guilds/{guild}/game-nights/games/seed", null)).EnsureSuccessStatusCode();
        var games = (await admin.GetFromJsonAsync<List<GameDto>>($"/guilds/{guild}/game-nights/games"))!;
        var ev = await Bot.PostAsJsonAsync($"/guilds/{guild}/events", new CreateEventRequest(9000, "Game night", "in 3 hours", 62000 + guild % 1000));
        ev.EnsureSuccessStatusCode();
        var eventId = (await ev.Content.ReadFromJsonAsync<EventDto>())!.Id;
        if (withPost)
        {
            // Give the event a posted message, as the bot would after it posts the embed.
            (await Bot.PutAsJsonAsync($"/events/{eventId}/message", new SetEventMessageRequest(62000 + guild % 1000, 555000 + guild, "general"))).EnsureSuccessStatusCode();
        }

        return new Night(guild, admin, member, session.UserId,
            games.Single(g => g.Spec.Key == "root"), games.Single(g => g.Spec.Key == "arcs"), eventId);
    }

    private static async Task<MySignalsDto> SetAsync(HttpClient client, string url, SignalKind kind)
    {
        var response = await client.PutAsJsonAsync(url, new SetSignalRequest(kind));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MySignalsDto>())!;
    }

    private async Task<List<Delivery>> PendingSyncsAsync(Guid eventId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var all = await db.Deliveries.AsNoTracking()
            .Where(d => d.Type == DeliveryType.SyncEventMessage && d.Status == DeliveryStatus.Pending).ToListAsync();
        return [.. all.Where(d => JsonSerializer.Deserialize<SyncEventMessagePayload>(d.PayloadJson)!.EventId == eventId)];
    }
}
