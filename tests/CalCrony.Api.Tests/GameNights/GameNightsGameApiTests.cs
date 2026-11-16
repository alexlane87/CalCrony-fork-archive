using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.GameNights;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests.GameNights;

/// <summary>Game Nights on, and the games file applied to one server at startup.</summary>
public sealed class GameNightsSeededFixture : WebAuthFixture
{
    public const long SeededGuild = 56001;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("GameNights:Enabled", "true");
        builder.UseSetting("GameNights:SeedGuildIds", $"{SeededGuild}, 56002");
    }
}

/// <summary>Step 3: the game library. A web server manager is Admin tier (so it can act as an organiser and
/// as an admin); a plain web member is the below-organiser caller.</summary>
public class GameNightsGameApiTests(GameNightsFixture fixture) : IClassFixture<GameNightsFixture>
{
    private HttpClient Bot => fixture.Client;

    private static long nextGuild = 55000;

    private static long NewGuildId() => Interlocked.Increment(ref nextGuild);

    private sealed record Arrangement(long Guild, HttpClient Admin, HttpClient Member);

    private static GameSpec Chess(string key = "chess", string name = "Chess") => new(
        key, name, GameResultType.SingleWinner, 2, 2, 2, false, 32, 1000,
        [
            new("players", "Players", GameFieldKind.Player, GameFieldScope.Table, true),
            new("opening", "Opening", GameFieldKind.Text, GameFieldScope.Table, false),
        ]);

    [Fact]
    public async Task Applying_the_games_file_gives_root_and_arcs_with_the_right_questions_and_is_idempotent()
    {
        var a = await ArrangeAsync();

        var first = await SeedAsync(a);
        Assert.Equal(new SeedGamesResponse(2, 0, 0), first);

        var games = await ListAsync(a, a.Member);
        Assert.Equal(["Arcs", "Root"], games.Select(g => g.Spec.Name));
        var root = games.Single(g => g.Spec.Key == "root").Spec;
        Assert.Equal(["players", "faction", "score", "left_out", "seat", "map", "landmarks", "deck"], root.Fields.Select(f => f.Key));
        Assert.Equal([true, true, true, false, false, false, false, false], root.Fields.Select(f => f.Required));
        Assert.Equal(GameFieldKind.PickList, root.Fields.Single(f => f.Key == "faction").Kind);
        Assert.Contains("Marquise de Cat", root.Fields.Single(f => f.Key == "faction").Values!);
        var arcs = games.Single(g => g.Spec.Key == "arcs").Spec;
        Assert.Equal([("players", true), ("colour", true), ("score", true), ("leader", false), ("round_ended", false)],
            arcs.Fields.Select(f => (f.Key, f.Required)));

        var second = await SeedAsync(a);
        Assert.Equal(new SeedGamesResponse(0, 0, 2), second);
        Assert.Equal(2, (await ListAsync(a, a.Member)).Count);
    }

    [Fact]
    public async Task Only_admins_can_apply_the_file_and_members_can_read_but_not_write()
    {
        var a = await ArrangeAsync();
        await SeedAsync(a);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await a.Member.PostAsync($"/guilds/{a.Guild}/game-nights/games/seed", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await a.Member.PostAsJsonAsync($"/guilds/{a.Guild}/game-nights/games", Chess())).StatusCode);
        var games = await ListAsync(a, a.Member);
        Assert.Equal(HttpStatusCode.OK, (await a.Member.GetAsync($"/guilds/{a.Guild}/game-nights/games/{games[0].Id}")).StatusCode);
    }

    [Fact]
    public async Task A_game_can_be_created_read_edited_and_retired()
    {
        var a = await ArrangeAsync();

        var created = await a.Admin.PostAsJsonAsync($"/guilds/{a.Guild}/game-nights/games", Chess());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var game = (await created.Content.ReadFromJsonAsync<GameDto>())!;
        Assert.True(game.Active);
        Assert.Equal(["players", "opening"], game.Spec.Fields.Select(f => f.Key));

        // Edit: rename, drop a question, add one, change one in place.
        var edited = Chess() with
        {
            Name = "Chess (blitz)",
            Fields =
            [
                new("players", "Players", GameFieldKind.Player, GameFieldScope.Table, true),
                new("clock", "Clock", GameFieldKind.PickList, GameFieldScope.Table, true, Values: ["Blitz", "Rapid"]),
            ],
        };
        var put = await a.Admin.PutAsJsonAsync($"/guilds/{a.Guild}/game-nights/games/{game.Id}", edited);
        put.EnsureSuccessStatusCode();
        var after = (await put.Content.ReadFromJsonAsync<GameDto>())!;
        Assert.Equal("Chess (blitz)", after.Spec.Name);
        Assert.Equal(["players", "clock"], after.Spec.Fields.Select(f => f.Key));

        // Retire: gone from the default list, still there on request, and back on reinstate.
        (await a.Admin.PostAsync($"/guilds/{a.Guild}/game-nights/games/{game.Id}/retire", null)).EnsureSuccessStatusCode();
        Assert.Empty(await ListAsync(a, a.Member));
        Assert.Single(await ListAsync(a, a.Member, includeRetired: true));
        (await a.Admin.PostAsync($"/guilds/{a.Guild}/game-nights/games/{game.Id}/reinstate", null)).EnsureSuccessStatusCode();
        Assert.Single(await ListAsync(a, a.Member));
    }

    [Fact]
    public async Task Bad_definitions_duplicate_keys_and_key_changes_are_refused()
    {
        var a = await ArrangeAsync();
        var games = $"/guilds/{a.Guild}/game-nights/games";

        var bad = await a.Admin.PostAsJsonAsync(games, Chess("Bad Key", " "));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("game key", (await bad.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);

        var tooBig = await a.Admin.PostAsJsonAsync(games, Chess() with { MaxSize = 101 });
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);

        var game = (await (await a.Admin.PostAsJsonAsync(games, Chess())).Content.ReadFromJsonAsync<GameDto>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await a.Admin.PostAsJsonAsync(games, Chess())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await a.Admin.PutAsJsonAsync($"{games}/{game.Id}", Chess("draughts"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await a.Admin.GetAsync($"{games}/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task A_game_belongs_to_its_server()
    {
        var a = await ArrangeAsync();
        var other = await ArrangeAsync();
        var game = (await (await a.Admin.PostAsJsonAsync($"/guilds/{a.Guild}/game-nights/games", Chess())).Content.ReadFromJsonAsync<GameDto>())!;

        Assert.Equal(HttpStatusCode.NotFound,
            (await other.Admin.GetAsync($"/guilds/{other.Guild}/game-nights/games/{game.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.Admin.PostAsync($"/guilds/{other.Guild}/game-nights/games/{game.Id}/retire", null)).StatusCode);
        Assert.Empty(await ListAsync(other, other.Member));
    }

    [Fact]
    public async Task Game_types_are_admin_managed_and_a_game_may_only_use_listed_ones()
    {
        var a = await ArrangeAsync();
        var types = $"/guilds/{a.Guild}/game-nights/game-types";
        var games = $"/guilds/{a.Guild}/game-nights/games";

        Assert.Equal(HttpStatusCode.Forbidden,
            (await a.Member.PutAsJsonAsync(types, new PutGameTypesRequest([new(9001, "Board games")]))).StatusCode);

        // A game can't name a type nobody has marked.
        var unknown = await a.Admin.PostAsJsonAsync(games, Chess() with { TypeRoleIds = [9001] });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        (await a.Admin.PutAsJsonAsync(types, new PutGameTypesRequest([new(9001, "Board games"), new(9002, "Card games")])))
            .EnsureSuccessStatusCode();
        var listed = await a.Member.GetFromJsonAsync<List<GameTypeDto>>(types);
        Assert.Equal(["Board games", "Card games"], listed!.Select(t => t.Label));

        var game = (await (await a.Admin.PostAsJsonAsync(games, Chess() with { TypeRoleIds = [9001], RoleId = 9100 }))
            .Content.ReadFromJsonAsync<GameDto>())!;
        Assert.Equal([9001L], game.Spec.TypeRoleIds);
        Assert.Equal(9100L, game.Spec.RoleId);

        // Dropping a type a game still uses is refused; dropping an unused one is fine.
        Assert.Equal(HttpStatusCode.Conflict,
            (await a.Admin.PutAsJsonAsync(types, new PutGameTypesRequest([new(9002, "Card games")]))).StatusCode);
        (await a.Admin.PutAsJsonAsync(types, new PutGameTypesRequest([new(9001, "Board games")]))).EnsureSuccessStatusCode();

        // Bad input.
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.PutAsJsonAsync(types,
            new PutGameTypesRequest([new(1, "A"), new(1, "B")]))).StatusCode);
    }

    [Fact]
    public async Task A_newer_file_version_refreshes_an_untouched_game_but_never_one_edited_by_hand()
    {
        var a = await ArrangeAsync();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<GameSeeder>();

        SeedFile File(int version, string name) => GameSeedFile.Parse($$"""
            { "games": [ { "version": {{version}}, "key": "go", "name": "{{name}}", "resultType": "SingleWinner",
              "minSize": 2, "maxSize": 2, "defaultSize": 2, "ratingEnabled": false, "ratingK": 32, "startRating": 1000,
              "fields": [ { "key": "players", "label": "Players", "kind": "Player", "scope": "Table", "required": true } ] } ] }
            """);

        Assert.Equal(new SeedGamesResponse(1, 0, 0), await seeder.ApplyAsync(a.Guild, File(1, "Go"), default));
        Assert.Equal(new SeedGamesResponse(0, 0, 1), await seeder.ApplyAsync(a.Guild, File(1, "Go"), default));
        Assert.Equal(new SeedGamesResponse(0, 1, 0), await seeder.ApplyAsync(a.Guild, File(2, "Go (new)"), default));
        Assert.Equal("Go (new)", Assert.Single(await ListAsync(a, a.Member)).Spec.Name);

        // Someone edits it by hand; a later file version must not overwrite that.
        var game = Assert.Single(await ListAsync(a, a.Member));
        (await a.Admin.PutAsJsonAsync($"/guilds/{a.Guild}/game-nights/games/{game.Id}", game.Spec with { Name = "Weiqi" }))
            .EnsureSuccessStatusCode();
        Assert.Equal(new SeedGamesResponse(0, 0, 1), await seeder.ApplyAsync(a.Guild, File(3, "Go (newest)"), default));
        Assert.Equal("Weiqi", Assert.Single(await ListAsync(a, a.Member)).Spec.Name);
    }

    [Fact]
    public async Task A_refresh_keeps_the_role_and_types_an_admin_attached()
    {
        var a = await ArrangeAsync();
        await a.Admin.PutAsJsonAsync($"/guilds/{a.Guild}/game-nights/game-types", new PutGameTypesRequest([new(9001, "Board games")]));
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<GameSeeder>();
        string Json(int version) => $$"""
            { "games": [ { "version": {{version}}, "key": "go", "name": "Go", "resultType": "SingleWinner",
              "minSize": 2, "maxSize": 2, "defaultSize": 2, "ratingEnabled": false, "ratingK": 32, "startRating": 1000,
              "fields": [ { "key": "players", "label": "Players", "kind": "Player", "scope": "Table", "required": true } ] } ] }
            """;
        await seeder.ApplyAsync(a.Guild, GameSeedFile.Parse(Json(1)), default);

        // The links are attached through the game's own edit, which marks it as edited; so a refresh skips it.
        // The role/type survive either way, which is what this checks.
        var game = Assert.Single(await ListAsync(a, a.Member));
        (await a.Admin.PutAsJsonAsync($"/guilds/{a.Guild}/game-nights/games/{game.Id}",
            game.Spec with { RoleId = 9100, TypeRoleIds = [9001] })).EnsureSuccessStatusCode();
        await seeder.ApplyAsync(a.Guild, GameSeedFile.Parse(Json(2)), default);

        var after = Assert.Single(await ListAsync(a, a.Member)).Spec;
        Assert.Equal(9100L, after.RoleId);
        Assert.Equal([9001L], after.TypeRoleIds);
    }

    // ---- helpers ----

    private async Task<Arrangement> ArrangeAsync()
    {
        var guild = NewGuildId();
        (await Bot.PutAsJsonAsync($"/guilds/{guild}/presence", new GuildPresenceRequest(true, "Games test guild"))).EnsureSuccessStatusCode();
        var (admin, _) = await fixture.LoginAsync(guild * 1000 + 1, (guild, "G", true));
        var (member, _) = await fixture.LoginAsync(guild * 1000 + 2, (guild, "G", false));
        return new Arrangement(guild, admin, member);
    }

    private static async Task<SeedGamesResponse> SeedAsync(Arrangement a)
    {
        var response = await a.Admin.PostAsync($"/guilds/{a.Guild}/game-nights/games/seed", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SeedGamesResponse>())!;
    }

    private static async Task<List<GameDto>> ListAsync(Arrangement a, HttpClient client, bool includeRetired = false) =>
        (await client.GetFromJsonAsync<List<GameDto>>($"/guilds/{a.Guild}/game-nights/games?includeRetired={includeRetired}"))!;
}

/// <summary>The games file is applied at startup to the servers listed in GameNights:SeedGuildIds.</summary>
public class GameNightsStartupSeedTests(GameNightsSeededFixture fixture) : IClassFixture<GameNightsSeededFixture>
{
    [Fact]
    public async Task Listed_servers_get_their_games_at_startup_and_others_do_not()
    {
        var seeded = await WaitForGamesAsync(GameNightsSeededFixture.SeededGuild);
        Assert.Equal(["Arcs", "Root"], seeded.Select(g => g.Spec.Name));

        var second = await WaitForGamesAsync(56002);
        Assert.Equal(2, second.Count);

        var untouched = await fixture.Client.GetFromJsonAsync<List<GameDto>>("/guilds/56003/game-nights/games");
        Assert.Empty(untouched!);
    }

    // Seeding runs in the background once the host has started, so give it a moment.
    private async Task<List<GameDto>> WaitForGamesAsync(long guild)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var games = await fixture.Client.GetFromJsonAsync<List<GameDto>>($"/guilds/{guild}/game-nights/games");
            if (games!.Count > 0)
            {
                return games;
            }

            await Task.Delay(200);
        }

        return [];
    }
}
