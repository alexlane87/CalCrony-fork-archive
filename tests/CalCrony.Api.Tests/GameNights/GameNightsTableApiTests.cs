using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests.GameNights;

/// <summary>Step 4: tables at a game night. A web server manager is Admin tier (so it can organise); a
/// plain web member is the caller who can only look after themselves.</summary>
public class GameNightsTableApiTests(GameNightsFixture fixture) : IClassFixture<GameNightsFixture>
{
    private HttpClient Bot => fixture.Client;

    private static long nextGuild = 57000;

    private static long NewGuildId() => Interlocked.Increment(ref nextGuild);

    private sealed record Night(long Guild, HttpClient Organiser, HttpClient Member, long MemberId, GameDto Root, Guid EventId);

    [Fact]
    public async Task An_organiser_opens_a_table_with_the_games_default_size_and_it_queues_exactly_one_thread()
    {
        var n = await ArrangeAsync();

        var table = await CreateAsync(n, new CreateTableRequest(n.Root.Id, SeatUserIds: [11, 12]));

        Assert.Equal(("Root", 1, 4, TableStatus.Open), (table.GameName, table.Number, table.Size, table.Status));
        Assert.Equal([11L, 12L], table.Seats.Select(s => s.UserId));
        Assert.Null(table.HostUserId);
        Assert.Equal(GameNightsVisibility.Public, table.Visibility);

        var queued = await ThreadDeliveriesAsync(table.Id);
        var delivery = Assert.Single(queued);
        Assert.Equal(DeliveryType.CreateTableThread, delivery.Type);

        // Reading the table back, or changing it, never queues a second thread.
        await n.Organiser.GetAsync($"/guilds/{n.Guild}/game-nights/tables/{table.Id}");
        (await n.Organiser.PatchAsJsonAsync($"/guilds/{n.Guild}/game-nights/tables/{table.Id}", new UpdateTableRequest(Size: 5))).EnsureSuccessStatusCode();
        Assert.Single(await ThreadDeliveriesAsync(table.Id));
    }

    [Fact]
    public async Task Tables_are_numbered_per_night_and_a_second_table_gets_number_two()
    {
        var n = await ArrangeAsync();

        var first = await CreateAsync(n, new CreateTableRequest(n.Root.Id));
        var second = await CreateAsync(n, new CreateTableRequest(n.Root.Id));

        Assert.Equal([1, 2], new[] { first.Number, second.Number });
        var list = await n.Member.GetFromJsonAsync<EventTablesDto>($"/guilds/{n.Guild}/game-nights/events/{n.EventId}/tables");
        Assert.Equal([1, 2], list!.Tables.Select(t => t.Number));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(101, false)]
    public async Task A_table_must_be_inside_the_games_size_range(int size, bool allowed)
    {
        var n = await ArrangeAsync();

        var response = await n.Organiser.PostAsJsonAsync(
            $"/guilds/{n.Guild}/game-nights/events/{n.EventId}/tables", new CreateTableRequest(n.Root.Id, Size: size));

        Assert.Equal(allowed ? HttpStatusCode.Created : HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_night_can_set_a_default_size_and_a_table_can_override_it()
    {
        var n = await ArrangeAsync();
        (await n.Organiser.PutAsJsonAsync($"/guilds/{n.Guild}/game-nights/events/{n.EventId}/table-defaults",
            new SetTableDefaultsRequest(3))).EnsureSuccessStatusCode();

        var byNight = await CreateAsync(n, new CreateTableRequest(n.Root.Id));
        var overridden = await CreateAsync(n, new CreateTableRequest(n.Root.Id, Size: 5));

        Assert.Equal([3, 5], new[] { byNight.Size, overridden.Size });
        var list = await n.Member.GetFromJsonAsync<EventTablesDto>($"/guilds/{n.Guild}/game-nights/events/{n.EventId}/tables");
        Assert.Equal(3, list!.DefaultTableSize);

        // A night default outside a game's range is pulled into it (Arcs holds 2 to 4).
        (await n.Organiser.PutAsJsonAsync($"/guilds/{n.Guild}/game-nights/events/{n.EventId}/table-defaults",
            new SetTableDefaultsRequest(6))).EnsureSuccessStatusCode();
        var arcs = await GetGameAsync(n, "arcs");
        Assert.Equal(4, (await CreateAsync(n, new CreateTableRequest(arcs.Id))).Size);

        Assert.Equal(HttpStatusCode.BadRequest, (await n.Organiser.PutAsJsonAsync(
            $"/guilds/{n.Guild}/game-nights/events/{n.EventId}/table-defaults", new SetTableDefaultsRequest(1))).StatusCode);
    }

    [Fact]
    public async Task A_host_is_optional_always_seated_and_gives_up_hosting_by_leaving()
    {
        var n = await ArrangeAsync();

        var hosted = await CreateAsync(n, new CreateTableRequest(n.Root.Id, HostUserId: 21, SeatUserIds: [22]));
        Assert.Equal(21, hosted.HostUserId);
        Assert.Equal([21L, 22L], hosted.Seats.Select(s => s.UserId)); // the host was seated first

        // A host has to be sitting down.
        var notSeated = await n.Organiser.PatchAsJsonAsync(
            $"/guilds/{n.Guild}/game-nights/tables/{hosted.Id}", new UpdateTableRequest(HostUserId: 99));
        Assert.Equal(HttpStatusCode.BadRequest, notSeated.StatusCode);

        var moved = await PatchAsync(n, hosted.Id, new UpdateTableRequest(HostUserId: 22));
        Assert.Equal(22, moved.HostUserId);

        // The host stands up: no host any more.
        var left = await n.Organiser.DeleteAsync($"/guilds/{n.Guild}/game-nights/tables/{hosted.Id}/seats/22");
        left.EnsureSuccessStatusCode();
        Assert.Null((await left.Content.ReadFromJsonAsync<TableDto>())!.HostUserId);

        var cleared = await PatchAsync(n, (await CreateAsync(n, new CreateTableRequest(n.Root.Id, HostUserId: 31))).Id, new UpdateTableRequest(ClearHost: true));
        Assert.Null(cleared.HostUserId);
    }

    [Fact]
    public async Task Seating_the_same_person_twice_is_rejected_and_a_full_table_turns_people_away()
    {
        var n = await ArrangeAsync();
        var table = await CreateAsync(n, new CreateTableRequest(n.Root.Id, Size: 2, SeatUserIds: [41]));
        var seats = $"/guilds/{n.Guild}/game-nights/tables/{table.Id}/seats";

        Assert.Equal(HttpStatusCode.Conflict, (await n.Organiser.PostAsJsonAsync(seats, new SeatRequest(41))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await n.Organiser.PostAsJsonAsync(seats, new SeatRequest(42))).StatusCode);
        var full = await n.Organiser.PostAsJsonAsync(seats, new SeatRequest(43));
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.Contains("full", (await full.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);

        // Cannot shrink below the seated count, and cannot be made too many people at creation.
        Assert.Equal(HttpStatusCode.Conflict, (await n.Organiser.PatchAsJsonAsync(
            $"/guilds/{n.Guild}/game-nights/tables/{table.Id}", new UpdateTableRequest(Size: 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await n.Organiser.PostAsJsonAsync(
            $"/guilds/{n.Guild}/game-nights/events/{n.EventId}/tables",
            new CreateTableRequest(n.Root.Id, Size: 2, SeatUserIds: [1, 2, 3]))).StatusCode);
    }

    [Fact]
    public async Task A_member_can_sit_and_stand_themselves_but_not_other_people()
    {
        var n = await ArrangeAsync();
        var table = await CreateAsync(n, new CreateTableRequest(n.Root.Id, SeatUserIds: [51]));
        var seats = $"/guilds/{n.Guild}/game-nights/tables/{table.Id}/seats";

        Assert.Equal(HttpStatusCode.Forbidden, (await n.Member.PostAsJsonAsync(seats, new SeatRequest(52))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await n.Member.DeleteAsync($"{seats}/51")).StatusCode);

        var sat = await n.Member.PostAsJsonAsync(seats, new SeatRequest(n.MemberId));
        Assert.Equal(HttpStatusCode.OK, sat.StatusCode);
        Assert.Contains(n.MemberId, (await sat.Content.ReadFromJsonAsync<TableDto>())!.Seats.Select(s => s.UserId));

        Assert.Equal(HttpStatusCode.OK, (await n.Member.DeleteAsync($"{seats}/{n.MemberId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await n.Member.DeleteAsync($"{seats}/{n.MemberId}")).StatusCode);
    }

    [Fact]
    public async Task Only_organisers_can_open_change_or_cancel_tables()
    {
        var n = await ArrangeAsync();
        var table = await CreateAsync(n, new CreateTableRequest(n.Root.Id));
        var tables = $"/guilds/{n.Guild}/game-nights";

        Assert.Equal(HttpStatusCode.Forbidden, (await n.Member.PostAsJsonAsync(
            $"{tables}/events/{n.EventId}/tables", new CreateTableRequest(n.Root.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await n.Member.PatchAsJsonAsync(
            $"{tables}/tables/{table.Id}", new UpdateTableRequest(Size: 3))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await n.Member.PostAsync($"{tables}/tables/{table.Id}/cancel", null)).StatusCode);

        var cancelled = await n.Organiser.PostAsync($"{tables}/tables/{table.Id}/cancel", null);
        cancelled.EnsureSuccessStatusCode();
        Assert.Equal(TableStatus.Cancelled, (await cancelled.Content.ReadFromJsonAsync<TableDto>())!.Status);

        // A cancelled table is closed to seats and edits, and drops out of the default list.
        Assert.Equal(HttpStatusCode.Conflict, (await n.Member.PostAsJsonAsync(
            $"{tables}/tables/{table.Id}/seats", new SeatRequest(n.MemberId))).StatusCode);
        var list = await n.Member.GetFromJsonAsync<EventTablesDto>($"{tables}/events/{n.EventId}/tables");
        Assert.Empty(list!.Tables);
        var all = await n.Member.GetFromJsonAsync<EventTablesDto>($"{tables}/events/{n.EventId}/tables?includeCancelled=true");
        Assert.Single(all!.Tables);
    }

    [Fact]
    public async Task A_retired_game_cannot_be_played_and_a_game_from_another_server_is_refused()
    {
        var n = await ArrangeAsync();
        var other = await ArrangeAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await n.Organiser.PostAsJsonAsync(
            $"/guilds/{n.Guild}/game-nights/events/{n.EventId}/tables", new CreateTableRequest(other.Root.Id))).StatusCode);

        (await n.Organiser.PostAsync($"/guilds/{n.Guild}/game-nights/games/{n.Root.Id}/retire", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await n.Organiser.PostAsJsonAsync(
            $"/guilds/{n.Guild}/game-nights/events/{n.EventId}/tables", new CreateTableRequest(n.Root.Id))).StatusCode);
    }

    [Fact]
    public async Task A_table_belongs_to_its_server()
    {
        var n = await ArrangeAsync();
        var other = await ArrangeAsync();
        var table = await CreateAsync(n, new CreateTableRequest(n.Root.Id));

        Assert.Equal(HttpStatusCode.NotFound,
            (await other.Organiser.GetAsync($"/guilds/{other.Guild}/game-nights/tables/{table.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.Organiser.GetAsync($"/guilds/{other.Guild}/game-nights/events/{n.EventId}/tables")).StatusCode);
    }

    [Fact]
    public async Task Seat_changes_queue_a_note_only_once_the_table_has_a_thread()
    {
        var n = await ArrangeAsync();
        var table = await CreateAsync(n, new CreateTableRequest(n.Root.Id));
        var seats = $"/guilds/{n.Guild}/game-nights/tables/{table.Id}/seats";

        await n.Organiser.PostAsJsonAsync(seats, new SeatRequest(61));
        Assert.Empty(await DeliveriesAsync(table.Id, DeliveryType.TableSeatChange)); // the thread post will mention them

        (await Bot.PutAsJsonAsync($"/guilds/{n.Guild}/game-nights/tables/{table.Id}/thread", new SetTableThreadRequest(777))).EnsureSuccessStatusCode();
        await n.Organiser.PostAsJsonAsync(seats, new SeatRequest(62));
        await n.Organiser.DeleteAsync($"{seats}/61");

        var notes = await DeliveriesAsync(table.Id, DeliveryType.TableSeatChange);
        Assert.Equal(2, notes.Count);
        Assert.Equal([true, false], notes.Select(d => JsonSerializer.Deserialize<TableSeatChangePayload>(d.PayloadJson)!.Joined));
    }

    [Fact]
    public async Task Only_the_bot_records_the_thread()
    {
        var n = await ArrangeAsync();
        var table = await CreateAsync(n, new CreateTableRequest(n.Root.Id));
        var url = $"/guilds/{n.Guild}/game-nights/tables/{table.Id}/thread";

        Assert.Equal(HttpStatusCode.Forbidden, (await n.Organiser.PutAsJsonAsync(url, new SetTableThreadRequest(1))).StatusCode);
        var set = await Bot.PutAsJsonAsync(url, new SetTableThreadRequest(888));
        Assert.Equal(888L, (await set.Content.ReadFromJsonAsync<TableDto>())!.ThreadId);
    }

    [Fact]
    public async Task A_hidden_table_is_not_found_for_a_member_who_is_not_sitting_at_it()
    {
        var n = await ArrangeAsync();
        var table = await CreateAsync(n, new CreateTableRequest(n.Root.Id, SeatUserIds: [n.MemberId]));
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            (await db.GameNightTables.FirstAsync(t => t.Id == table.Id)).Visibility = GameNightsVisibility.InviteOnly;
            await db.SaveChangesAsync();
        }

        var url = $"/guilds/{n.Guild}/game-nights/tables/{table.Id}";
        // The seated member still sees it; an organiser (not moderator) does not for invite-only.
        Assert.Equal(HttpStatusCode.OK, (await n.Member.GetAsync(url)).StatusCode);
        var other = await fixture.LoginAsync(n.Guild * 1000 + 9, (n.Guild, "G", false));
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync(url)).StatusCode);
        var list = await other.Client.GetFromJsonAsync<EventTablesDto>($"/guilds/{n.Guild}/game-nights/events/{n.EventId}/tables");
        Assert.Empty(list!.Tables);
    }

    // ---- helpers ----

    private async Task<Night> ArrangeAsync()
    {
        var guild = NewGuildId();
        (await Bot.PutAsJsonAsync($"/guilds/{guild}/presence", new GuildPresenceRequest(true, "Tables test guild"))).EnsureSuccessStatusCode();
        var (organiser, _) = await fixture.LoginAsync(guild * 1000 + 1, (guild, "G", true));
        var (member, session) = await fixture.LoginAsync(guild * 1000 + 2, (guild, "G", false));
        (await organiser.PostAsync($"/guilds/{guild}/game-nights/games/seed", null)).EnsureSuccessStatusCode();
        var games = (await organiser.GetFromJsonAsync<List<GameDto>>($"/guilds/{guild}/game-nights/games"))!;
        var ev = await Bot.PostAsJsonAsync($"/guilds/{guild}/events", new CreateEventRequest(9000, "Game night", "in 3 hours", 58000 + guild % 1000));
        ev.EnsureSuccessStatusCode();
        var eventId = (await ev.Content.ReadFromJsonAsync<EventDto>())!.Id;
        return new Night(guild, organiser, member, session.UserId, games.Single(g => g.Spec.Key == "root"), eventId);
    }

    private static Task<GameDto> GetGameAsync(Night n, string key) =>
        n.Organiser.GetFromJsonAsync<List<GameDto>>($"/guilds/{n.Guild}/game-nights/games")
            .ContinueWith(t => t.Result!.Single(g => g.Spec.Key == key));

    private static async Task<TableDto> CreateAsync(Night n, CreateTableRequest request)
    {
        var response = await n.Organiser.PostAsJsonAsync($"/guilds/{n.Guild}/game-nights/events/{n.EventId}/tables", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TableDto>())!;
    }

    private static async Task<TableDto> PatchAsync(Night n, Guid tableId, UpdateTableRequest request)
    {
        var response = await n.Organiser.PatchAsJsonAsync($"/guilds/{n.Guild}/game-nights/tables/{tableId}", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TableDto>())!;
    }

    private Task<List<Delivery>> ThreadDeliveriesAsync(Guid tableId) => DeliveriesAsync(tableId, DeliveryType.CreateTableThread);

    private async Task<List<Delivery>> DeliveriesAsync(Guid tableId, DeliveryType type)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var all = await db.Deliveries.AsNoTracking().Where(d => d.Type == type).OrderBy(d => d.CreatedAt).ToListAsync();
        return [.. all.Where(d => d.PayloadJson.Contains(tableId.ToString(), StringComparison.OrdinalIgnoreCase))];
    }
}
