using System.Net;
using System.Net.Http.Json;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.AspNetCore.Hosting;

namespace CalCrony.Api.Tests.GameNights;

/// <summary>WebAuthFixture with the Game Nights feature flag switched on.</summary>
public sealed class GameNightsFixture : WebAuthFixture
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("GameNights:Enabled", "true");
    }
}

/// <summary>Step 1: the role-to-tier mapping and the per-tier gate. Tier is read per request from
/// the bot's role snapshot (ADR 0004); a server manager is Admin; an unverifiable snapshot refuses
/// anything above Member with 409 instead of guessing.</summary>
public class GameNightsTierApiTests(GameNightsFixture fixture) : IClassFixture<GameNightsFixture>
{
    private const long OrganiserRole = 5001;
    private const long ModeratorRole = 5002;
    private const long AdminRole = 5003;

    private HttpClient Bot => fixture.Client;

    // One guild per test keeps snapshots independent (the fixture is shared across the class).
    private static long nextGuild = 51000;

    private static long NewGuildId() => Interlocked.Increment(ref nextGuild);

    [Fact]
    public async Task Each_probe_is_open_to_its_tier_and_above_and_closed_below()
    {
        var guild = await ArrangeGuildAsync();
        var member = await LoginAsync(guild, 1, manage: false);
        var organiser = await LoginAsync(guild, 2, manage: false);
        var moderator = await LoginAsync(guild, 3, manage: false);
        var admin = await LoginAsync(guild, 4, manage: false);
        await MapAndSyncAsync(guild,
            members: [(organiser.UserId, [OrganiserRole]), (moderator.UserId, [ModeratorRole]), (admin.UserId, [AdminRole])]);

        // probe -> expected status for member, organiser, moderator, admin
        var matrix = new (string Probe, HttpStatusCode[] Expected)[]
        {
            ("member", [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK]),
            ("organiser", [HttpStatusCode.Forbidden, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK]),
            ("moderator", [HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.OK, HttpStatusCode.OK]),
            ("admin", [HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.OK]),
        };
        var callers = new[] { member, organiser, moderator, admin };
        foreach (var (probe, expected) in matrix)
        {
            for (var i = 0; i < callers.Length; i++)
            {
                var response = await callers[i].Client.GetAsync($"/guilds/{guild}/game-nights/probe/{probe}");
                Assert.True(expected[i] == response.StatusCode,
                    $"probe {probe}, caller #{i} ({(GameNightsTier)Math.Min(i, 3)} tier): expected {expected[i]}, got {response.StatusCode}");
            }
        }
    }

    [Fact]
    public async Task The_highest_mapped_role_wins()
    {
        var guild = await ArrangeGuildAsync();
        var both = await LoginAsync(guild, 11, manage: false);
        await MapAndSyncAsync(guild, members: [(both.UserId, [OrganiserRole, ModeratorRole])]);

        var me = await both.Client.GetFromJsonAsync<MyTierResponse>($"/guilds/{guild}/game-nights/me");

        Assert.Equal(GameNightsTier.Moderator, me!.Tier);
        Assert.True(me.Verified);
    }

    [Fact]
    public async Task A_member_with_no_mapped_role_is_a_member()
    {
        var guild = await ArrangeGuildAsync();
        var plain = await LoginAsync(guild, 21, manage: false);
        await MapAndSyncAsync(guild, members: []);

        var me = await plain.Client.GetFromJsonAsync<MyTierResponse>($"/guilds/{guild}/game-nights/me");

        Assert.Equal(GameNightsTier.Member, me!.Tier);
        Assert.True(me.Verified);
    }

    [Fact]
    public async Task With_no_mapping_everyone_is_a_member_and_a_server_manager_is_admin()
    {
        var guild = await ArrangeGuildAsync();
        var plain = await LoginAsync(guild, 31, manage: false);
        var manager = await LoginAsync(guild, 32, manage: true);

        Assert.Equal(GameNightsTier.Member, (await Me(plain, guild)).Tier);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await plain.Client.GetAsync($"/guilds/{guild}/game-nights/probe/organiser")).StatusCode);

        // The bootstrap path: a manager can configure a fresh server before any role is mapped.
        Assert.Equal(GameNightsTier.Admin, (await Me(manager, guild)).Tier);
        Assert.Equal(HttpStatusCode.OK,
            (await manager.Client.GetAsync($"/guilds/{guild}/game-nights/probe/admin")).StatusCode);
    }

    [Fact]
    public async Task Someone_outside_the_guild_gets_403_and_the_bot_passes_every_gate()
    {
        var guild = await ArrangeGuildAsync();
        var outsider = await LoginAsync(NewGuildId(), 41, manage: true); // manages a different guild

        Assert.Equal(HttpStatusCode.Forbidden,
            (await outsider.Client.GetAsync($"/guilds/{guild}/game-nights/probe/member")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await Bot.GetAsync($"/guilds/{guild}/game-nights/probe/admin")).StatusCode);
    }

    [Fact]
    public async Task A_guild_that_has_not_synced_since_the_mapping_fails_closed_above_member()
    {
        var guild = await ArrangeGuildAsync();
        var organiser = await LoginAsync(guild, 51, manage: false);
        // Map, but never sync: the roles have not been checked, so the tier is unknown.
        await PutMappingAsync(guild);

        Assert.Equal(HttpStatusCode.OK,
            (await organiser.Client.GetAsync($"/guilds/{guild}/game-nights/probe/member")).StatusCode);
        var refused = await organiser.Client.GetAsync($"/guilds/{guild}/game-nights/probe/organiser");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var error = await refused.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal(GameNightsErrorCodes.TierUnverifiable, error!.Code);

        var me = await Me(organiser, guild);
        Assert.False(me.Verified);
        Assert.Equal(GameNightsTier.Member, me.Tier);
    }

    [Fact]
    public async Task Only_admins_can_read_or_change_the_mapping()
    {
        var guild = await ArrangeGuildAsync();
        var organiser = await LoginAsync(guild, 61, manage: false);
        var manager = await LoginAsync(guild, 62, manage: true);
        await MapAndSyncAsync(guild, members: [(organiser.UserId, [OrganiserRole])]);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await organiser.Client.GetAsync($"/guilds/{guild}/game-nights/tier-roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await organiser.Client.PutAsJsonAsync(
            $"/guilds/{guild}/game-nights/tier-roles", new PutTierRolesRequest([]))).StatusCode);

        var read = await manager.Client.GetFromJsonAsync<TierRolesResponse>($"/guilds/{guild}/game-nights/tier-roles");
        Assert.Equal(
            [AdminRole, ModeratorRole, OrganiserRole],
            read!.Roles.Select(r => r.RoleId));
    }

    [Theory]
    [InlineData(0, GameNightsTier.Admin)]            // not a real role id
    [InlineData(7001, GameNightsTier.Member)]        // Member is everyone's default, not mappable
    [InlineData(7001, (GameNightsTier)9)]            // not a tier
    public async Task The_mapping_rejects_bad_entries(long roleId, GameNightsTier tier)
    {
        var guild = await ArrangeGuildAsync();

        var response = await Bot.PutAsJsonAsync(
            $"/guilds/{guild}/game-nights/tier-roles", new PutTierRolesRequest([new TierRoleDto(roleId, tier)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_role_cannot_map_to_two_tiers()
    {
        var guild = await ArrangeGuildAsync();

        var response = await Bot.PutAsJsonAsync($"/guilds/{guild}/game-nights/tier-roles", new PutTierRolesRequest(
            [new TierRoleDto(7002, GameNightsTier.Organiser), new TierRoleDto(7002, GameNightsTier.Admin)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Mapped_roles_join_the_bots_watched_set_and_leave_it_when_unmapped()
    {
        var guild = await ArrangeGuildAsync();
        await PutMappingAsync(guild);

        var watched = await Bot.GetFromJsonAsync<GuildWatchedRolesDto>($"/guilds/{guild}/roles/watched");
        Assert.Equal([OrganiserRole, ModeratorRole, AdminRole], watched!.RoleIds.Order());

        await Bot.PutAsJsonAsync($"/guilds/{guild}/game-nights/tier-roles", new PutTierRolesRequest([]));
        var after = await Bot.GetFromJsonAsync<GuildWatchedRolesDto>($"/guilds/{guild}/roles/watched");
        Assert.Empty(after!.RoleIds);
    }

    [Fact]
    public async Task A_deleted_mapped_role_stops_counting()
    {
        var guild = await ArrangeGuildAsync();
        var organiser = await LoginAsync(guild, 71, manage: false);
        await PutMappingAsync(guild);
        // The bot reports the organiser role as gone (null name) and the other two as present.
        (await Bot.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(OrganiserRole, null), new RoleNameDto(ModeratorRole, "Mod"), new RoleNameDto(AdminRole, "Boss")],
            [new MemberRolesDto(organiser.UserId, [OrganiserRole])]))).EnsureSuccessStatusCode();

        var me = await Me(organiser, guild);

        Assert.Equal(GameNightsTier.Member, me.Tier);
        Assert.True(me.Verified);
    }

    // ---- helpers ----

    private sealed record Caller(HttpClient Client, long UserId);

    private async Task<long> ArrangeGuildAsync()
    {
        var guild = NewGuildId();
        (await Bot.PutAsJsonAsync($"/guilds/{guild}/presence", new GuildPresenceRequest(true, "Test guild")))
            .EnsureSuccessStatusCode();
        return guild;
    }

    private async Task<Caller> LoginAsync(long guild, long userId, bool manage)
    {
        var (client, session) = await fixture.LoginAsync(guild * 1000 + userId, (guild, "Test guild", manage));
        return new Caller(client, session.UserId);
    }

    private static async Task<MyTierResponse> Me(Caller caller, long guild) =>
        (await caller.Client.GetFromJsonAsync<MyTierResponse>($"/guilds/{guild}/game-nights/me"))!;

    private async Task PutMappingAsync(long guild) =>
        (await Bot.PutAsJsonAsync($"/guilds/{guild}/game-nights/tier-roles", new PutTierRolesRequest(
        [
            new TierRoleDto(OrganiserRole, GameNightsTier.Organiser),
            new TierRoleDto(ModeratorRole, GameNightsTier.Moderator),
            new TierRoleDto(AdminRole, GameNightsTier.Admin),
        ]))).EnsureSuccessStatusCode();

    private async Task MapAndSyncAsync(long guild, (long UserId, long[] RoleIds)[] members)
    {
        await PutMappingAsync(guild);
        (await Bot.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(OrganiserRole, "Organiser"), new RoleNameDto(ModeratorRole, "Moderator"), new RoleNameDto(AdminRole, "Admin")],
            [.. members.Select(m => new MemberRolesDto(m.UserId, m.RoleIds))]))).EnsureSuccessStatusCode();
    }
}

/// <summary>The flag is off by default: a deployment that has not opted in serves no Game Nights routes.</summary>
public class GameNightsFlagOffTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Routes_do_not_exist_unless_the_flag_is_on()
    {
        var response = await fixture.Client.GetAsync("/guilds/1/game-nights/probe/member");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
