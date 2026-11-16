using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests.GameNights;

/// <summary>Step 5: a game's follow role (attach, create through the bot, follow and unfollow) and the game-type
/// roles. A web server manager is Admin tier; a plain web member is the caller below organiser.</summary>
public class GameNightsRoleApiTests(GameNightsFixture fixture) : IClassFixture<GameNightsFixture>
{
    private HttpClient Bot => fixture.Client;

    private static long nextGuild = 59000;

    private static long NewGuildId() => Interlocked.Increment(ref nextGuild);

    private sealed record Setup(long Guild, HttpClient Admin, HttpClient Member, long MemberId, GameDto Game);

    private static GameSpec Chess(long? role = null) => new(
        "chess", "Chess", GameResultType.SingleWinner, 2, 2, 2, false, 32, 1000,
        [new("players", "Players", GameFieldKind.Player, GameFieldScope.Table, true)], role);

    // ---- attach ----

    [Fact]
    public async Task Attaching_a_role_sets_it_makes_it_watched_and_refuses_a_role_another_game_has()
    {
        var s = await ArrangeAsync();

        s.Admin.PutAsJsonAsync($"/guilds/{s.Guild}/game-nights/games/{s.Game.Id}", Chess(role: 7001)).Result.EnsureSuccessStatusCode();
        var got = await s.Member.GetFromJsonAsync<GameDto>($"/guilds/{s.Guild}/game-nights/games/{s.Game.Id}");
        Assert.Equal(7001L, got!.Spec.RoleId);

        var watched = await Bot.GetFromJsonAsync<GuildWatchedRolesDto>($"/guilds/{s.Guild}/roles/watched");
        Assert.Contains(7001L, watched!.RoleIds);

        var second = Chess(role: 7001) with { Key = "go", Name = "Go" };
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PostAsJsonAsync($"/guilds/{s.Guild}/game-nights/games", second)).StatusCode);
        var other = await s.Admin.PostAsJsonAsync($"/guilds/{s.Guild}/game-nights/games", second with { RoleId = 7002 });
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    // ---- create through the bot ----

    [Fact]
    public async Task Asking_for_a_role_marks_it_pending_and_queues_exactly_one_creation()
    {
        var s = await ArrangeAsync();
        var url = $"/guilds/{s.Guild}/game-nights/games/{s.Game.Id}/role/create";

        var asked = await s.Admin.PostAsync(url, null);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        Assert.Equal(GameRoleState.Pending, (await asked.Content.ReadFromJsonAsync<GameDto>())!.RoleState);

        await s.Admin.PostAsync(url, null); // asking again while pending does not queue a second one
        var queued = await DeliveriesAsync(DeliveryType.CreateGameRole, s.Game.Id);
        var payload = JsonSerializer.Deserialize<CreateGameRolePayload>(Assert.Single(queued).PayloadJson)!;
        Assert.Equal(("Chess", s.Guild), (payload.Name, payload.GuildId));

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Member.PostAsync(url, null)).StatusCode);
    }

    [Fact]
    public async Task The_bot_reports_the_role_it_made_and_the_game_has_it()
    {
        var s = await ArrangeAsync();
        await s.Admin.PostAsync($"/guilds/{s.Guild}/game-nights/games/{s.Game.Id}/role/create", null);

        var done = await Bot.PutAsJsonAsync($"/guilds/{s.Guild}/game-nights/games/{s.Game.Id}/role-status",
            new SetGameRoleStatusRequest(7100, GameRoleState.None));

        done.EnsureSuccessStatusCode();
        var game = (await done.Content.ReadFromJsonAsync<GameDto>())!;
        Assert.Equal((7100L, GameRoleState.None), (game.Spec.RoleId, game.RoleState));
        Assert.Contains(7100L, (await Bot.GetFromJsonAsync<GuildWatchedRolesDto>($"/guilds/{s.Guild}/roles/watched"))!.RoleIds);
        // With a role it can't be asked for again.
        Assert.Equal(HttpStatusCode.Conflict,
            (await s.Admin.PostAsync($"/guilds/{s.Guild}/game-nights/games/{s.Game.Id}/role/create", null)).StatusCode);
    }

    [Fact]
    public async Task A_failed_attempt_keeps_the_reason_for_the_admin_and_can_be_retried()
    {
        var s = await ArrangeAsync();
        var url = $"/guilds/{s.Guild}/game-nights/games/{s.Game.Id}";
        await s.Admin.PostAsync($"{url}/role/create", null);

        (await Bot.PutAsJsonAsync($"{url}/role-status", new SetGameRoleStatusRequest(
            null, GameRoleState.Failed, "I need the Manage Roles permission."))).EnsureSuccessStatusCode();
        var failed = await s.Member.GetFromJsonAsync<GameDto>(url);
        Assert.Equal((GameRoleState.Failed, "I need the Manage Roles permission."), (failed!.RoleState, failed.RoleNote));
        Assert.Null(failed.Spec.RoleId);

        // Asking again queues a fresh attempt and clears the note.
        Assert.Equal(HttpStatusCode.Accepted, (await s.Admin.PostAsync($"{url}/role/create", null)).StatusCode);
        Assert.Null((await s.Member.GetFromJsonAsync<GameDto>(url))!.RoleNote);
        Assert.Equal(2, (await DeliveriesAsync(DeliveryType.CreateGameRole, s.Game.Id)).Count);
    }

    [Fact]
    public async Task Only_the_bot_can_report_a_role_and_pending_is_not_a_thing_to_report()
    {
        var s = await ArrangeAsync();
        var url = $"/guilds/{s.Guild}/game-nights/games/{s.Game.Id}/role-status";

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Admin.PutAsJsonAsync(url, new SetGameRoleStatusRequest(1, GameRoleState.None))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Bot.PutAsJsonAsync(url, new SetGameRoleStatusRequest(null, GameRoleState.Pending))).StatusCode);
    }

    // ---- follow ----

    [Fact]
    public async Task A_member_following_a_game_queues_the_role_for_the_bot_to_give()
    {
        var s = await ArrangeAsync();
        var url = $"/guilds/{s.Guild}/game-nights/games/{s.Game.Id}/follow";

        // No role yet: nothing to follow.
        Assert.Equal(HttpStatusCode.Conflict, (await s.Member.PostAsJsonAsync(url, new FollowGameRequest(true))).StatusCode);

        (await s.Admin.PutAsJsonAsync($"/guilds/{s.Guild}/game-nights/games/{s.Game.Id}", Chess(role: 7200))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Accepted, (await s.Member.PostAsJsonAsync(url, new FollowGameRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await s.Member.PostAsJsonAsync(url, new FollowGameRequest(false))).StatusCode);

        var queued = (await DeliveriesAsync(DeliveryType.GameRoleFollow, s.Game.Id))
            .Select(d => JsonSerializer.Deserialize<GameRoleFollowPayload>(d.PayloadJson)!).ToList();
        Assert.Equal([true, false], queued.Select(p => p.Follow));
        Assert.All(queued, p => Assert.Equal((s.MemberId, 7200L, s.Guild), (p.UserId, p.RoleId, p.GuildId)));

        // The bot gives roles itself; this route is for the web.
        Assert.Equal(HttpStatusCode.BadRequest, (await Bot.PostAsJsonAsync(url, new FollowGameRequest(true))).StatusCode);
    }

    [Fact]
    public async Task The_web_learns_who_follows_what_from_the_role_snapshot_and_says_so_when_it_cannot_tell()
    {
        var s = await ArrangeAsync();
        await s.Admin.PutAsJsonAsync($"/guilds/{s.Guild}/game-nights/games/{s.Game.Id}", Chess(role: 7300));
        var following = $"/guilds/{s.Guild}/game-nights/games/following";

        // The role is newly watched and the bot has not checked it: unknown, not "you don't follow".
        var before = await s.Member.GetFromJsonAsync<FollowingResponse>(following);
        Assert.False(before!.Known);

        (await Bot.PutAsJsonAsync($"/guilds/{s.Guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(7300, "Chess fans")], [new MemberRolesDto(s.MemberId, [7300])]))).EnsureSuccessStatusCode();
        var after = await s.Member.GetFromJsonAsync<FollowingResponse>(following);
        Assert.True(after!.Known);
        Assert.Equal([s.Game.Id], after.GameIds);

        // Someone who does not hold the role follows nothing, and that is a known answer.
        (await Bot.PutAsJsonAsync($"/guilds/{s.Guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(7300, "Chess fans")], []))).EnsureSuccessStatusCode();
        var none = await s.Member.GetFromJsonAsync<FollowingResponse>(following);
        Assert.True(none!.Known);
        Assert.Empty(none.GameIds);
    }

    // ---- game types ----

    [Fact]
    public async Task Game_type_roles_are_watched_so_their_holders_are_snapshotted()
    {
        var s = await ArrangeAsync();

        (await s.Admin.PutAsJsonAsync($"/guilds/{s.Guild}/game-nights/game-types",
            new PutGameTypesRequest([new(7400, "Board games")]))).EnsureSuccessStatusCode();

        var watched = await Bot.GetFromJsonAsync<GuildWatchedRolesDto>($"/guilds/{s.Guild}/roles/watched");
        Assert.Contains(7400L, watched!.RoleIds);
    }

    // ---- helpers ----

    private async Task<Setup> ArrangeAsync()
    {
        var guild = NewGuildId();
        (await Bot.PutAsJsonAsync($"/guilds/{guild}/presence", new GuildPresenceRequest(true, "Roles test guild"))).EnsureSuccessStatusCode();
        var (admin, _) = await fixture.LoginAsync(guild * 1000 + 1, (guild, "G", true));
        var (member, session) = await fixture.LoginAsync(guild * 1000 + 2, (guild, "G", false));
        var created = await admin.PostAsJsonAsync($"/guilds/{guild}/game-nights/games", Chess());
        created.EnsureSuccessStatusCode();
        return new Setup(guild, admin, member, session.UserId, (await created.Content.ReadFromJsonAsync<GameDto>())!);
    }

    private async Task<List<Delivery>> DeliveriesAsync(DeliveryType type, Guid gameId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var all = await db.Deliveries.AsNoTracking().Where(d => d.Type == type).OrderBy(d => d.CreatedAt).ToListAsync();
        return [.. all.Where(d => d.PayloadJson.Contains(gameId.ToString(), StringComparison.OrdinalIgnoreCase))];
    }
}
