using System.Text.RegularExpressions;

namespace CalCrony.Contracts.GameNights;

/// <summary>How a game's result is recorded. Stored and sent as the number.</summary>
public enum GameResultType
{
    /// <summary>Every player gets a placement (ties allowed).</summary>
    RankedPlacement = 0,

    /// <summary>One winner; the rest are not ranked.</summary>
    SingleWinner = 1,

    /// <summary>Players are on two sides; placement 1 marks the winning side.</summary>
    WinningSide = 2,
}

/// <summary>What kind of value a result field holds.</summary>
public enum GameFieldKind
{
    /// <summary>The roster of players (always table-level; the players themselves come with the result).</summary>
    Player = 0,

    /// <summary>One value from a fixed list.</summary>
    PickList = 1,

    /// <summary>A whole number.</summary>
    Number = 2,

    /// <summary>Free text.</summary>
    Text = 3,
}

/// <summary>Whether a field is answered once for the table or once per player.</summary>
public enum GameFieldScope
{
    /// <summary>One answer for the whole table (map, deck, round the game ended).</summary>
    Table = 0,

    /// <summary>One answer per player (faction, colour, score).</summary>
    Player = 1,
}

/// <summary>One question a result report asks.</summary>
/// <param name="Key">A short stable id, lower-case letters, digits and underscores (for example
/// <c>faction</c>). Results store answers by key, so a key is never reused for something else.</param>
/// <param name="Label">What the reporter sees.</param>
/// <param name="Kind">The kind of answer.</param>
/// <param name="Scope">Once per table or once per player.</param>
/// <param name="Required">Whether the report cannot be submitted without it.</param>
/// <param name="Unique">For per-player pick-lists and text: no two players may give the same answer
/// (two players cannot both be the Marquise, or both be red).</param>
/// <param name="Values">The allowed values of a pick-list; empty for other kinds.</param>
public record GameFieldSpec(
    string Key, string Label, GameFieldKind Kind, GameFieldScope Scope, bool Required, bool Unique = false,
    IReadOnlyList<string>? Values = null);

/// <summary>Everything that defines a game. Games are configuration, not code: the seed file, the
/// create and edit routes and the validator all use this one shape.</summary>
/// <param name="Key">A short stable id, lower-case letters, digits and underscores (for example <c>root</c>).</param>
/// <param name="Name">The game's name.</param>
/// <param name="ResultType">How its result is recorded.</param>
/// <param name="MinSize">The fewest players at a table (at least 2).</param>
/// <param name="MaxSize">The most players at a table (at most 100).</param>
/// <param name="DefaultSize">The size a new table starts at.</param>
/// <param name="RatingEnabled">Whether results feed a league rating.</param>
/// <param name="RatingK">The rating K-factor.</param>
/// <param name="StartRating">The rating a player starts at.</param>
/// <param name="Fields">The result questions, in order.</param>
/// <param name="RoleId">The Discord role members take to follow the game, if it has one.</param>
/// <param name="TypeRoleIds">The game-type roles the game belongs to (each must be marked as a game type).</param>
public record GameSpec(
    string Key, string Name, GameResultType ResultType, int MinSize, int MaxSize, int DefaultSize,
    bool RatingEnabled, int RatingK, int StartRating, IReadOnlyList<GameFieldSpec> Fields,
    long? RoleId = null, IReadOnlyList<long>? TypeRoleIds = null);

/// <summary>A game as stored.</summary>
/// <param name="Id">The game id.</param>
/// <param name="Active">False once retired: it cannot be chosen for new tables.</param>
/// <param name="Spec">The game's definition.</param>
public record GameDto(Guid Id, bool Active, GameSpec Spec);

/// <summary>A Discord role an admin has marked as a game type (for example "Board games").</summary>
/// <param name="RoleId">The Discord role id.</param>
/// <param name="Label">A name for the type, as the admin typed it.</param>
public record GameTypeDto(long RoleId, string Label);

/// <summary>Replaces a server's game-type list wholesale.</summary>
/// <param name="Types">The full list.</param>
public record PutGameTypesRequest(IReadOnlyList<GameTypeDto> Types);

/// <summary>What applying the games config file to a server did.</summary>
/// <param name="Created">Games added.</param>
/// <param name="Updated">Games refreshed because the file has a newer version of them.</param>
/// <param name="Skipped">Games left alone (already current, or edited locally since).</param>
public record SeedGamesResponse(int Created, int Updated, int Skipped);

/// <summary>The rules every game definition must meet.</summary>
public static partial class GameRules
{
    /// <summary>Smallest allowed table.</summary>
    public const int AbsoluteMinSize = 2;

    /// <summary>Largest allowed table.</summary>
    public const int AbsoluteMaxSize = 100;

    /// <summary>Most result questions one game may have.</summary>
    public const int MaxFields = 30;

    /// <summary>Most values a pick-list may have.</summary>
    public const int MaxPickListValues = 50;

    /// <summary>Longest key.</summary>
    public const int KeyLength = 40;

    /// <summary>Longest game name.</summary>
    public const int NameLength = 100;

    /// <summary>Longest field label.</summary>
    public const int LabelLength = 80;

    /// <summary>Longest pick-list value or text answer.</summary>
    public const int ValueLength = 60;

    /// <summary>Longest free-text answer.</summary>
    public const int TextAnswerLength = 200;

    /// <summary>Most games one server may have (active or retired).</summary>
    public const int MaxGamesPerGuild = 200;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex KeyPattern();

    /// <summary>Whether a string is a valid key.</summary>
    /// <param name="key">The candidate key.</param>
    /// <returns>True for a lower-case, underscore-separated key of at most 40 characters.</returns>
    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>Checks a game definition.</summary>
    /// <param name="game">The definition.</param>
    /// <returns>The problems found, empty when it is acceptable.</returns>
    public static IReadOnlyList<string> Validate(GameSpec game)
    {
        var problems = new List<string>();

        if (!IsValidKey(game.Key))
        {
            problems.Add("The game key must be lower-case letters, digits and underscores, starting with a letter (40 characters at most).");
        }

        if (string.IsNullOrWhiteSpace(game.Name) || game.Name.Trim().Length > NameLength)
        {
            problems.Add($"The game name is required and can be at most {NameLength} characters.");
        }

        if (!Enum.IsDefined(game.ResultType))
        {
            problems.Add("The result type is not recognised.");
        }

        if (game.MinSize < AbsoluteMinSize || game.MaxSize > AbsoluteMaxSize || game.MinSize > game.MaxSize)
        {
            problems.Add($"Table sizes must run from at least {AbsoluteMinSize} to at most {AbsoluteMaxSize}, smallest first.");
        }
        else if (game.DefaultSize < game.MinSize || game.DefaultSize > game.MaxSize)
        {
            problems.Add("The default table size must be within the game's size range.");
        }

        if (game.RatingK is < 1 or > 400 || game.StartRating is < 0 or > 10_000)
        {
            problems.Add("The rating K must be 1 to 400 and the start rating 0 to 10000.");
        }

        if (game.RoleId is <= 0)
        {
            problems.Add("The role id is not valid.");
        }

        ValidateFields(game.Fields, problems);
        return problems;
    }

    private static void ValidateFields(IReadOnlyList<GameFieldSpec>? fields, List<string> problems)
    {
        if (fields is null)
        {
            problems.Add("The field list is required.");
            return;
        }

        if (fields.Count > MaxFields)
        {
            problems.Add($"A game can have at most {MaxFields} result questions.");
        }

        var seen = new HashSet<string>();
        var rosters = 0;
        foreach (var field in fields)
        {
            var where = $"Question \"{field.Key}\"";
            if (!IsValidKey(field.Key))
            {
                problems.Add($"{where}: the key must be lower-case letters, digits and underscores.");
            }
            else if (!seen.Add(field.Key))
            {
                problems.Add($"{where}: the key is used twice.");
            }

            if (string.IsNullOrWhiteSpace(field.Label) || field.Label.Trim().Length > LabelLength)
            {
                problems.Add($"{where}: the label is required and can be at most {LabelLength} characters.");
            }

            if (!Enum.IsDefined(field.Kind) || !Enum.IsDefined(field.Scope))
            {
                problems.Add($"{where}: the kind or scope is not recognised.");
                continue;
            }

            if (field.Kind == GameFieldKind.Player)
            {
                rosters++;
                if (field.Scope != GameFieldScope.Table || !field.Required)
                {
                    problems.Add($"{where}: the player list is always required and table-wide.");
                }
            }

            var values = field.Values ?? [];
            if (field.Kind == GameFieldKind.PickList)
            {
                if (values.Count < 2 || values.Count > MaxPickListValues
                    || values.Any(v => string.IsNullOrWhiteSpace(v) || v.Trim().Length > ValueLength)
                    || values.Select(v => v.Trim().ToLowerInvariant()).Distinct().Count() != values.Count)
                {
                    problems.Add($"{where}: a pick-list needs 2 to {MaxPickListValues} distinct values of at most {ValueLength} characters.");
                }
            }
            else if (values.Count > 0)
            {
                problems.Add($"{where}: only a pick-list has values.");
            }

            if (field.Unique && (field.Scope != GameFieldScope.Player || field.Kind is not (GameFieldKind.PickList or GameFieldKind.Text)))
            {
                problems.Add($"{where}: 'unique' only applies to per-player pick-lists and text.");
            }
        }

        if (rosters > 1)
        {
            problems.Add("A game can have only one player list.");
        }
    }
}
