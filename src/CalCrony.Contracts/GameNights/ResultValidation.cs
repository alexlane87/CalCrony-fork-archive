using System.Globalization;

namespace CalCrony.Contracts.GameNights;

/// <summary>One player in a result report. Exactly one of <paramref name="UserId"/> and
/// <paramref name="GuestId"/> is set: a Discord member, or a guest with no Discord account.</summary>
/// <param name="UserId">The Discord user id of a member.</param>
/// <param name="GuestId">The id of a guest record.</param>
/// <param name="Placement">Finishing place, 1 is best; ties share a place. See <see cref="ResultValidator"/>
/// for what each result type requires.</param>
/// <param name="Values">This player's answers to the per-player questions, by field key (a number is sent
/// as text, for example "87").</param>
public record SubmittedPlayer(
    long? UserId, Guid? GuestId, int? Placement, IReadOnlyDictionary<string, string>? Values = null);

/// <summary>A result report, before it is stored.</summary>
/// <param name="Players">Who played.</param>
/// <param name="TableValues">The answers to the table-wide questions, by field key.</param>
public record ResultSubmission(IReadOnlyList<SubmittedPlayer> Players, IReadOnlyDictionary<string, string>? TableValues = null);

/// <summary>One thing wrong with a report.</summary>
/// <param name="Field">The question's key, or null when the problem is about the report as a whole.</param>
/// <param name="Message">What is wrong, in words a reporter can act on.</param>
public record ValidationProblem(string? Field, string Message);

/// <summary>Checks a result report against its game's definition. Pure and shared: the API runs it before
/// storing a result (Step 7), and the bot and web run it to catch mistakes before submitting.
/// <para>Rules: the player count is within the game's table sizes and nobody is listed twice; placements
/// follow the result type (ranked: everyone has one, from 1 to the player count, with at least one 1;
/// single winner: exactly one player has placement 1; winning side: everyone has 1 or 2, with both sides
/// present); required questions are answered; pick-list answers are on the list; numbers are whole;
/// text is not too long; a "unique" question has no repeated answer; unknown question keys are rejected.</para></summary>
public static class ResultValidator
{
    /// <summary>Validates a report.</summary>
    /// <param name="game">The game's definition.</param>
    /// <param name="submission">The report.</param>
    /// <returns>The problems found, empty when the report is acceptable.</returns>
    public static IReadOnlyList<ValidationProblem> Validate(GameSpec game, ResultSubmission submission)
    {
        var problems = new List<ValidationProblem>();
        var players = submission.Players ?? [];

        CheckRoster(game, players, problems);
        CheckPlacements(game, players, problems);
        CheckAnswers(game, submission, problems);
        return problems;
    }

    private static void CheckRoster(GameSpec game, IReadOnlyList<SubmittedPlayer> players, List<ValidationProblem> problems)
    {
        if (players.Count < game.MinSize || players.Count > game.MaxSize)
        {
            problems.Add(new(null, game.MinSize == game.MaxSize
                ? $"{game.Name} needs exactly {game.MinSize} players (you listed {players.Count})."
                : $"{game.Name} needs {game.MinSize} to {game.MaxSize} players (you listed {players.Count})."));
        }

        var seen = new HashSet<string>();
        foreach (var player in players)
        {
            if ((player.UserId is null) == (player.GuestId is null))
            {
                problems.Add(new(null, "Each player must be either a member or a guest."));
                continue;
            }

            var identity = player.UserId is { } user ? $"u{user}" : $"g{player.GuestId}";
            if (!seen.Add(identity))
            {
                problems.Add(new(null, "A player is listed twice."));
            }
        }
    }

    private static void CheckPlacements(GameSpec game, IReadOnlyList<SubmittedPlayer> players, List<ValidationProblem> problems)
    {
        var placements = players.Select(p => p.Placement).ToList();
        var count = players.Count;

        switch (game.ResultType)
        {
            case GameResultType.RankedPlacement:
                if (placements.Any(p => p is null))
                {
                    problems.Add(new(null, "Every player needs a finishing place."));
                }
                else if (placements.Any(p => p < 1 || p > count))
                {
                    problems.Add(new(null, $"Finishing places must be between 1 and {count}."));
                }
                else if (!placements.Contains(1))
                {
                    problems.Add(new(null, "Someone must have finished first."));
                }

                break;

            case GameResultType.SingleWinner:
                if (placements.Count(p => p == 1) != 1)
                {
                    problems.Add(new(null, "Exactly one player must be marked as the winner."));
                }

                break;

            case GameResultType.WinningSide:
                if (placements.Any(p => p is not (1 or 2)))
                {
                    problems.Add(new(null, "Mark each player as on the winning side (1) or the other side (2)."));
                }
                else if (!placements.Contains(1) || !placements.Contains(2))
                {
                    problems.Add(new(null, "Both the winning side and the other side need at least one player."));
                }

                break;
        }
    }

    private static void CheckAnswers(GameSpec game, ResultSubmission submission, List<ValidationProblem> problems)
    {
        var known = game.Fields.Select(f => f.Key).ToHashSet();
        var tableValues = submission.TableValues ?? new Dictionary<string, string>();
        foreach (var key in tableValues.Keys.Where(k => !known.Contains(k)))
        {
            problems.Add(new(key, $"\"{key}\" is not a question for {game.Name}."));
        }

        foreach (var key in (submission.Players ?? []).SelectMany(p => p.Values?.Keys ?? []).Where(k => !known.Contains(k)).Distinct())
        {
            problems.Add(new(key, $"\"{key}\" is not a question for {game.Name}."));
        }

        foreach (var field in game.Fields.Where(f => f.Kind != GameFieldKind.Player))
        {
            if (field.Scope == GameFieldScope.Table)
            {
                tableValues.TryGetValue(field.Key, out var value);
                CheckValue(field, value, who: null, problems);
                continue;
            }

            var answers = new List<string>();
            var index = 0;
            foreach (var player in submission.Players ?? [])
            {
                index++;
                string? value = null;
                player.Values?.TryGetValue(field.Key, out value);
                CheckValue(field, value, who: $"player {index}", problems);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    answers.Add(value.Trim());
                }
            }

            if (field.Unique && answers.Select(a => a.ToLowerInvariant()).Distinct().Count() != answers.Count)
            {
                problems.Add(new(field.Key, $"No two players can give the same {field.Label.ToLowerInvariant()}."));
            }
        }
    }

    private static void CheckValue(GameFieldSpec field, string? value, string? who, List<ValidationProblem> problems)
    {
        var label = who is null ? field.Label : $"{field.Label} for {who}";
        if (string.IsNullOrWhiteSpace(value))
        {
            if (field.Required)
            {
                problems.Add(new(field.Key, $"{label} is required."));
            }

            return;
        }

        var text = value.Trim();
        switch (field.Kind)
        {
            case GameFieldKind.PickList:
                if (!(field.Values ?? []).Any(v => string.Equals(v.Trim(), text, StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add(new(field.Key, $"{label}: \"{text}\" is not one of the choices."));
                }

                break;

            case GameFieldKind.Number:
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                {
                    problems.Add(new(field.Key, $"{label} must be a whole number."));
                }

                break;

            case GameFieldKind.Text:
                if (text.Length > GameRules.TextAnswerLength)
                {
                    problems.Add(new(field.Key, $"{label} can be at most {GameRules.TextAnswerLength} characters."));
                }

                break;
        }
    }
}
