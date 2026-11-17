using CalCrony.Contracts.GameNights;

namespace CalCrony.Api.GameNights;

/// <summary>Maps between the stored game entities and the shared <see cref="GameSpec"/> shape, so the
/// create route, the edit route and the config-file seeder all write games the same way.</summary>
public static class GameCatalog
{
    /// <summary>Projects a stored game (fields and type links loaded) to its DTO.</summary>
    /// <param name="game">The game.</param>
    /// <returns>The DTO.</returns>
    public static GameDto ToDto(GameNightGame game) => new(game.Id, game.Active, ToSpec(game), game.RoleState, game.RoleNote);

    /// <summary>Projects a stored game to its definition.</summary>
    /// <param name="game">The game (fields and type links loaded).</param>
    /// <returns>The definition.</returns>
    public static GameSpec ToSpec(GameNightGame game) => new(
        game.Key, game.Name, game.ResultType, game.MinSize, game.MaxSize, game.DefaultSize,
        game.RatingEnabled, game.RatingK, game.StartRating,
        [.. game.Fields.OrderBy(f => f.SortOrder).Select(f =>
            new GameFieldSpec(f.Key, f.Label, f.Kind, f.Scope, f.Required, f.Unique, [.. f.Values]))],
        game.RoleId,
        [.. game.TypeLinks.Select(l => l.TypeRoleId).Order()],
        game.PhotoRequired);

    /// <summary>Writes a definition onto a stored game. Fields are matched by key (updated in place,
    /// added, or removed) rather than replaced wholesale, so the edit never trips the unique
    /// (game, key) index. The role and type links are written only when <paramref name="includeLinks"/>
    /// is true; a config-file refresh leaves what an admin attached alone.</summary>
    /// <param name="game">The game to write to (fields and type links loaded).</param>
    /// <param name="spec">The definition.</param>
    /// <param name="includeLinks">Whether to write the role and game-type links.</param>
    public static void Apply(GameNightGame game, GameSpec spec, bool includeLinks)
    {
        game.Key = spec.Key;
        game.Name = spec.Name.Trim();
        game.ResultType = spec.ResultType;
        game.MinSize = spec.MinSize;
        game.MaxSize = spec.MaxSize;
        game.DefaultSize = spec.DefaultSize;
        game.RatingEnabled = spec.RatingEnabled;
        game.RatingK = spec.RatingK;
        game.StartRating = spec.StartRating;
        game.PhotoRequired = spec.PhotoRequired;

        var wanted = spec.Fields.Select(f => f.Key).ToHashSet();
        foreach (var gone in game.Fields.Where(f => !wanted.Contains(f.Key)).ToList())
        {
            game.Fields.Remove(gone);
        }

        for (var i = 0; i < spec.Fields.Count; i++)
        {
            var field = spec.Fields[i];
            var row = game.Fields.FirstOrDefault(f => f.Key == field.Key);
            if (row is null)
            {
                row = new GameNightGameField { Id = Guid.NewGuid(), GameId = game.Id, Key = field.Key };
                game.Fields.Add(row);
            }

            row.SortOrder = i;
            row.Label = field.Label.Trim();
            row.Kind = field.Kind;
            row.Scope = field.Scope;
            row.Required = field.Required;
            row.Unique = field.Unique;
            row.Values = [.. (field.Values ?? []).Select(v => v.Trim())];
        }

        if (!includeLinks)
        {
            return;
        }

        game.RoleId = spec.RoleId;
        var types = (spec.TypeRoleIds ?? []).Distinct().ToHashSet();
        foreach (var gone in game.TypeLinks.Where(l => !types.Contains(l.TypeRoleId)).ToList())
        {
            game.TypeLinks.Remove(gone);
        }

        foreach (var roleId in types.Where(id => game.TypeLinks.All(l => l.TypeRoleId != id)))
        {
            game.TypeLinks.Add(new GameNightGameTypeLink { GameId = game.Id, TypeRoleId = roleId });
        }
    }
}
