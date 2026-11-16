using System.Text;

namespace CalCrony.Contracts.GameNights;

/// <summary>Where a game's follow role stands. Stored and sent as the number.</summary>
public enum GameRoleState
{
    /// <summary>Nothing is pending: the game has a role, or deliberately has none.</summary>
    None = 0,

    /// <summary>An organiser asked for the role to be created and the bot has not done it yet.</summary>
    Pending = 1,

    /// <summary>The bot tried and could not (or could not grant it to a member); see the note.</summary>
    Failed = 2,
}

/// <summary>The bot records the outcome of making or granting a game's role (bot only).</summary>
/// <param name="RoleId">The role the bot made, or null to leave the game's role as it is.</param>
/// <param name="State">The new state.</param>
/// <param name="Note">Why it failed, for the state Failed.</param>
public record SetGameRoleStatusRequest(long? RoleId, GameRoleState State, string? Note = null);

/// <summary>Give or take the follow role for the caller.</summary>
/// <param name="Follow">True to follow the game (get its role), false to stop.</param>
public record FollowGameRequest(bool Follow);

/// <summary>Which games the caller follows, as far as the role snapshot can say.</summary>
/// <param name="Known">False when the snapshot cannot answer (the bot has not synced recently); then
/// <paramref name="GameIds"/> is empty and the page should not pretend to know.</param>
/// <param name="GameIds">The games whose role the caller holds.</param>
public record FollowingResponse(bool Known, IReadOnlyList<Guid> GameIds);

/// <summary>Outbox payload: create the Discord role for a game.</summary>
/// <param name="GameId">The game.</param>
/// <param name="GuildId">The Discord guild (server) id.</param>
/// <param name="Name">The role's name.</param>
public record CreateGameRolePayload(Guid GameId, long GuildId, string Name);

/// <summary>Outbox payload: give or take a game's role for a member.</summary>
/// <param name="GameId">The game.</param>
/// <param name="GuildId">The Discord guild (server) id.</param>
/// <param name="RoleId">The role.</param>
/// <param name="UserId">The member.</param>
/// <param name="Follow">True to give the role, false to take it.</param>
public record GameRoleFollowPayload(Guid GameId, long GuildId, long RoleId, long UserId, bool Follow);

/// <summary>How a game gets from a name and a few choices to a stored definition. Shared by the Discord
/// wizard and the web wizard so both build exactly the same thing.</summary>
public static class GameWizard
{
    /// <summary>The question sets a new game can start from. More can be added on the game afterwards.</summary>
    public enum FieldTemplate
    {
        /// <summary>Just the players (and their finishing places).</summary>
        PlayersOnly = 0,

        /// <summary>The players and a final score for each.</summary>
        PlayersAndScores = 1,
    }

    /// <summary>A key for a new game: the name in lower case with runs of other characters turned into
    /// underscores, made unique against the keys already in use by adding a number.</summary>
    /// <param name="name">The game's name.</param>
    /// <param name="taken">The keys already in use on the server.</param>
    /// <returns>A valid, unused key.</returns>
    public static string KeyFromName(string name, IEnumerable<string> taken)
    {
        var builder = new StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var stem = builder.ToString().Trim('_');
        if (stem.Length == 0 || !char.IsAsciiLetter(stem[0]))
        {
            stem = "game_" + stem;
        }

        stem = stem.Trim('_');
        stem = stem.Length > GameRules.KeyLength - 4 ? stem[..(GameRules.KeyLength - 4)].TrimEnd('_') : stem;
        var used = taken.ToHashSet();
        var key = stem;
        for (var n = 2; used.Contains(key); n++)
        {
            key = $"{stem}_{n}";
        }

        return key;
    }

    /// <summary>Builds a new game's definition: the chosen questions, no rating tuning beyond the usual
    /// defaults (K 32, start 1000, ratings on), and the role if one is attached.</summary>
    /// <param name="name">The game's name.</param>
    /// <param name="resultType">How its result is recorded.</param>
    /// <param name="minSize">The fewest players.</param>
    /// <param name="maxSize">The most players.</param>
    /// <param name="defaultSize">The starting table size (clamped into the range).</param>
    /// <param name="template">The starting questions.</param>
    /// <param name="roleId">The follow role, if one is already attached.</param>
    /// <param name="takenKeys">The keys already in use on the server.</param>
    /// <returns>The definition (the caller still runs <see cref="GameRules.Validate"/>).</returns>
    public static GameSpec Build(
        string name, GameResultType resultType, int minSize, int maxSize, int defaultSize, FieldTemplate template,
        long? roleId, IEnumerable<string> takenKeys)
    {
        List<GameFieldSpec> fields = [new("players", "Players", GameFieldKind.Player, GameFieldScope.Table, true)];
        if (template == FieldTemplate.PlayersAndScores)
        {
            fields.Add(new("score", "Final score", GameFieldKind.Number, GameFieldScope.Player, true));
        }

        var size = minSize <= maxSize ? Math.Clamp(defaultSize, minSize, maxSize) : defaultSize;
        return new GameSpec(
            KeyFromName(name, takenKeys), name.Trim(), resultType, minSize, maxSize, size,
            RatingEnabled: true, RatingK: 32, StartRating: 1000, fields, roleId);
    }
}
