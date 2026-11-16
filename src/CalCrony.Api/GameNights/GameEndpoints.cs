using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Endpoints;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.GameNights;

/// <summary>The game library: list, get, create, edit and retire games (organisers and above write, any
/// member reads), the admin-only game-type list, and the admin-only "apply the games config file".</summary>
public static class GameEndpoints
{
    /// <summary>Maps the game routes onto the Game Nights group.</summary>
    /// <param name="g">The <c>/guilds/{guildId}/game-nights</c> group.</param>
    public static void MapGameEndpoints(this RouteGroupBuilder g)
    {
        g.MapGet("/games", ListGames).RequireTier(GameNightsTier.Member);
        g.MapGet("/games/{gameId:guid}", GetGame).RequireTier(GameNightsTier.Member);
        g.MapPost("/games", CreateGame).RequireTier(GameNightsTier.Organiser);
        g.MapPut("/games/{gameId:guid}", UpdateGame).RequireTier(GameNightsTier.Organiser);
        g.MapPost("/games/{gameId:guid}/retire", (long guildId, Guid gameId, CalCronyDbContext db, CancellationToken ct) =>
            SetActiveAsync(guildId, gameId, false, db, ct)).RequireTier(GameNightsTier.Organiser);
        g.MapPost("/games/{gameId:guid}/reinstate", (long guildId, Guid gameId, CalCronyDbContext db, CancellationToken ct) =>
            SetActiveAsync(guildId, gameId, true, db, ct)).RequireTier(GameNightsTier.Organiser);
        g.MapPost("/games/seed", SeedGames).RequireTier(GameNightsTier.Admin);
        g.MapPost("/games/{gameId:guid}/role/create", RequestRole).RequireTier(GameNightsTier.Organiser);
        g.MapPut("/games/{gameId:guid}/role-status", SetRoleStatus).RequireAuthorization("BotOnly").RequireTier(GameNightsTier.Member);
        g.MapPost("/games/{gameId:guid}/follow", Follow).RequireTier(GameNightsTier.Member);
        g.MapGet("/games/following", Following).RequireTier(GameNightsTier.Member);

        g.MapGet("/game-types", ListGameTypes).RequireTier(GameNightsTier.Member);
        g.MapPut("/game-types", PutGameTypes).RequireTier(GameNightsTier.Admin);
    }

    private static IQueryable<GameNightGame> WithDetail(CalCronyDbContext db) =>
        db.GameNightGames.Include(x => x.Fields).Include(x => x.TypeLinks);

    private static async Task<IResult> ListGames(
        long guildId, CalCronyDbContext db, CancellationToken cancellationToken, bool includeRetired = false)
    {
        var games = await WithDetail(db).AsNoTracking()
            .Where(x => x.GuildId == guildId && (includeRetired || x.Active))
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        return Results.Ok(games.Select(GameCatalog.ToDto).ToList());
    }

    private static async Task<IResult> GetGame(long guildId, Guid gameId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var game = await WithDetail(db).AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == gameId && x.GuildId == guildId, cancellationToken);
        return game is null ? Results.NotFound() : Results.Ok(GameCatalog.ToDto(game));
    }

    private static async Task<IResult> CreateGame(
        long guildId, GameSpec spec, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (Problems(spec) is { } invalid)
        {
            return invalid;
        }

        if (await db.GameNightGames.CountAsync(x => x.GuildId == guildId, cancellationToken) >= GameRules.MaxGamesPerGuild)
        {
            return Bad($"A server can have at most {GameRules.MaxGamesPerGuild} games.");
        }

        if (await db.GameNightGames.AnyAsync(x => x.GuildId == guildId && x.Key == spec.Key, cancellationToken))
        {
            return Results.Conflict(new ErrorResponse($"There is already a game with the key \"{spec.Key}\"."));
        }

        if (await UnknownTypeRolesAsync(db, guildId, spec, cancellationToken) is { } unknown)
        {
            return unknown;
        }

        if (await RoleProblemAsync(db, guildId, spec.RoleId, exceptGameId: null, cancellationToken) is { } roleProblem)
        {
            return roleProblem;
        }

        var game = new GameNightGame { Id = Guid.NewGuid(), GuildId = guildId };
        db.GameNightGames.Add(game);
        GameCatalog.Apply(game, spec, includeLinks: true);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/guilds/{guildId}/game-nights/games/{game.Id}", GameCatalog.ToDto(game));
    }

    private static async Task<IResult> UpdateGame(
        long guildId, Guid gameId, GameSpec spec, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (Problems(spec) is { } invalid)
        {
            return invalid;
        }

        var game = await WithDetail(db).FirstOrDefaultAsync(x => x.Id == gameId && x.GuildId == guildId, cancellationToken);
        if (game is null)
        {
            return Results.NotFound();
        }

        // Results store answers by question key, so a game's own key never changes.
        if (spec.Key != game.Key)
        {
            return Bad("A game's key can't be changed.");
        }

        if (await UnknownTypeRolesAsync(db, guildId, spec, cancellationToken) is { } unknown)
        {
            return unknown;
        }

        if (spec.RoleId != game.RoleId)
        {
            if (await RoleProblemAsync(db, guildId, spec.RoleId, gameId, cancellationToken) is { } roleProblem)
            {
                return roleProblem;
            }

            game.RoleState = GameRoleState.None; // a new role (or none) starts clean
            game.RoleNote = null;
        }

        GameCatalog.Apply(game, spec, includeLinks: true);
        game.LocallyEdited = true;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(GameCatalog.ToDto(game));
    }

    private static async Task<IResult> SetActiveAsync(
        long guildId, Guid gameId, bool active, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var game = await WithDetail(db).FirstOrDefaultAsync(x => x.Id == gameId && x.GuildId == guildId, cancellationToken);
        if (game is null)
        {
            return Results.NotFound();
        }

        game.Active = active;
        game.LocallyEdited = true;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(GameCatalog.ToDto(game));
    }

    private static async Task<IResult> SeedGames(
        long guildId, GameSeeder seeder, IConfiguration configuration, CancellationToken cancellationToken)
    {
        SeedFile file;
        try
        {
            file = GameSeedFile.Load(configuration);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new ErrorResponse(ex.Message), statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Ok(await seeder.ApplyAsync(guildId, file, cancellationToken));
    }

    // ---- game types ----

    private static async Task<IResult> ListGameTypes(long guildId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var types = await db.GameNightGameTypes.AsNoTracking()
            .Where(t => t.GuildId == guildId)
            .OrderBy(t => t.Label)
            .Select(t => new GameTypeDto(t.RoleId, t.Label))
            .ToListAsync(cancellationToken);
        return Results.Ok(types);
    }

    private static async Task<IResult> PutGameTypes(
        long guildId, PutGameTypesRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (request.Types is null || request.Types.Count > 50
            || request.Types.Any(t => t.RoleId <= 0 || string.IsNullOrWhiteSpace(t.Label) || t.Label.Trim().Length > GameNightsModel.GameTypeLabelLength)
            || request.Types.GroupBy(t => t.RoleId).Any(group => group.Count() > 1))
        {
            return Bad($"Give up to 50 distinct roles, each with a label of at most {GameNightsModel.GameTypeLabelLength} characters.");
        }

        var keep = request.Types.Select(t => t.RoleId).ToList();
        var inUse = await (
            from link in db.GameNightGameTypeLinks
            join game in db.GameNightGames on link.GameId equals game.Id
            where game.GuildId == guildId && !keep.Contains(link.TypeRoleId)
            select link.TypeRoleId).AnyAsync(cancellationToken);
        if (inUse)
        {
            return Results.Conflict(new ErrorResponse("A type you removed is still used by a game. Take it off those games first."));
        }

        // Type roles are watched, so their holders are snapshotted: drop any leftovers for newly marked ones.
        await RoleSnapshotEndpoints.InvalidateNewlyWatchedAsync(db, guildId, keep, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.GameNightGameTypes.Where(t => t.GuildId == guildId).ExecuteDeleteAsync(cancellationToken);
        foreach (var type in request.Types)
        {
            db.GameNightGameTypes.Add(new GameNightGameType { GuildId = guildId, RoleId = type.RoleId, Label = type.Label.Trim() });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(request.Types.OrderBy(t => t.Label).ToList());
    }

    // ---- follow roles ----

    private static async Task<IResult> RequestRole(
        long guildId, Guid gameId, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var game = await WithDetail(db).FirstOrDefaultAsync(x => x.Id == gameId && x.GuildId == guildId, cancellationToken);
        if (game is null)
        {
            return Results.NotFound();
        }

        if (game.RoleId is not null)
        {
            return Results.Conflict(new ErrorResponse("This game already has a role."));
        }

        if (game.RoleState != GameRoleState.Pending)
        {
            game.RoleState = GameRoleState.Pending;
            game.RoleNote = null;
            GameNightOutbox.Enqueue(db, clock, DeliveryType.CreateGameRole, 0, new CreateGameRolePayload(game.Id, guildId, game.Name));
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.Accepted($"/guilds/{guildId}/game-nights/games/{game.Id}", GameCatalog.ToDto(game));
    }

    private static async Task<IResult> SetRoleStatus(
        long guildId, Guid gameId, SetGameRoleStatusRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.State) || request.State == GameRoleState.Pending)
        {
            return Bad("The bot reports a role as made (None) or failed.");
        }

        var game = await WithDetail(db).FirstOrDefaultAsync(x => x.Id == gameId && x.GuildId == guildId, cancellationToken);
        if (game is null)
        {
            return Results.NotFound();
        }

        if (request.RoleId is { } roleId)
        {
            if (await RoleProblemAsync(db, guildId, roleId, gameId, cancellationToken) is { } roleProblem)
            {
                return roleProblem;
            }

            game.RoleId = roleId;
        }

        game.RoleState = request.State;
        var note = request.State == GameRoleState.Failed ? request.Note?.Trim() : null;
        game.RoleNote = note is { Length: > GameNightsModel.RoleNoteLength } ? note[..GameNightsModel.RoleNoteLength] : note;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(GameCatalog.ToDto(game));
    }

    private static async Task<IResult> Follow(
        long guildId, Guid gameId, FollowGameRequest request, HttpContext context, CalCronyDbContext db, IClock clock,
        CancellationToken cancellationToken)
    {
        if (context.User.WebUserId() is not { } userId)
        {
            return Bad("Following is for signed-in members; the bot gives roles itself.");
        }

        var game = await db.GameNightGames.FirstOrDefaultAsync(x => x.Id == gameId && x.GuildId == guildId, cancellationToken);
        if (game is null)
        {
            return Results.NotFound();
        }

        if (!game.Active || game.RoleId is not { } roleId)
        {
            return Results.Conflict(new ErrorResponse("This game has no role to follow yet."));
        }

        GameNightOutbox.Enqueue(db, clock, DeliveryType.GameRoleFollow, 0, new GameRoleFollowPayload(game.Id, guildId, roleId, userId, request.Follow));
        await db.SaveChangesAsync(cancellationToken);
        return Results.Accepted();
    }

    private static async Task<IResult> Following(
        long guildId, HttpContext context, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var userId = context.User.WebUserId() ?? 0;
        var games = await db.GameNightGames.AsNoTracking()
            .Where(x => x.GuildId == guildId && x.Active && x.RoleId != null)
            .Select(x => new { x.Id, RoleId = x.RoleId!.Value })
            .ToListAsync(cancellationToken);
        if (userId == 0 || games.Count == 0)
        {
            return Results.Ok(new FollowingResponse(userId != 0, []));
        }

        var roleIds = games.Select(x => x.RoleId).ToList();
        var snapshot = await db.Guilds.AsNoTracking()
            .Where(x => x.Id == guildId)
            .Select(x => new
            {
                x.RolesSyncedAt,
                Checked = db.GuildRoles.Where(r => r.GuildId == guildId && roleIds.Contains(r.RoleId)).Select(r => r.RoleId).ToList(),
                Held = db.GuildMemberRoles.Where(m => m.GuildId == guildId && m.UserId == userId).Select(m => m.RoleIds).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Same rule as the signup restrictions: a stale marker, or a role nobody has checked yet, means the
        // snapshot cannot answer, so say "don't know" rather than "you don't follow".
        var known = snapshot is not null
            && RoleRestriction.IsSnapshotFresh(snapshot.RolesSyncedAt, clock.GetCurrentInstant())
            && roleIds.All(snapshot.Checked.Contains);
        if (!known)
        {
            return Results.Ok(new FollowingResponse(false, []));
        }

        var held = (snapshot!.Held ?? []).ToHashSet();
        return Results.Ok(new FollowingResponse(true, [.. games.Where(x => held.Contains(x.RoleId)).Select(x => x.Id)]));
    }

    // ---- helpers ----

    /// <summary>A game's role must not already belong to another game; a newly used role is dropped from the
    /// role snapshot first, so the web fails closed on it until the bot has checked it (ADR 0004).</summary>
    private static async Task<IResult?> RoleProblemAsync(
        CalCronyDbContext db, long guildId, long? roleId, Guid? exceptGameId, CancellationToken cancellationToken)
    {
        if (roleId is not { } role)
        {
            return null;
        }

        if (await db.GameNightGames.AnyAsync(x => x.GuildId == guildId && x.RoleId == role && x.Id != exceptGameId, cancellationToken))
        {
            return Results.Conflict(new ErrorResponse("That role already belongs to another game."));
        }

        await RoleSnapshotEndpoints.InvalidateNewlyWatchedAsync(db, guildId, [role], cancellationToken);
        return null;
    }


    private static IResult? Problems(GameSpec spec)
    {
        var problems = GameRules.Validate(spec);
        return problems.Count == 0 ? null : Bad(string.Join(" ", problems));
    }

    private static async Task<IResult?> UnknownTypeRolesAsync(
        CalCronyDbContext db, long guildId, GameSpec spec, CancellationToken cancellationToken)
    {
        var wanted = (spec.TypeRoleIds ?? []).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return null;
        }

        var known = await db.GameNightGameTypes
            .Where(t => t.GuildId == guildId && wanted.Contains(t.RoleId))
            .CountAsync(cancellationToken);
        return known == wanted.Count ? null : Bad("Every game type must be one an admin has marked as a game type.");
    }

    private static IResult Bad(string message) => Results.BadRequest(new ErrorResponse(message));
}
