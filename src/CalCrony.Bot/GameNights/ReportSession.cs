using System.Collections.Concurrent;
using System.Globalization;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;

namespace CalCrony.Bot.GameNights;

/// <summary>What a step of the report flow asks.</summary>
public enum ReportStepKind
{
    /// <summary>Who played.</summary>
    Players,

    /// <summary>How they finished (places, the winner, or sides, by the game's result type).</summary>
    Places,

    /// <summary>A per-player pick-list question (faction, colour).</summary>
    PlayerPick,

    /// <summary>A table-wide pick-list question (map, deck).</summary>
    TablePick,

    /// <summary>A form of up to five typed answers (scores, round, landmarks...).</summary>
    Inputs,

    /// <summary>The board photo (optional unless the game requires it).</summary>
    Photo,

    /// <summary>The summary, with Submit and Edit.</summary>
    Summary,
}

/// <summary>One typed answer in a form (a number or a short text), for one player or for the table.</summary>
/// <param name="FieldKey">The question's key.</param>
/// <param name="Label">What the reporter sees.</param>
/// <param name="PlayerId">The player it is about, or null for a table-wide answer.</param>
/// <param name="Required">Whether it must be filled in.</param>
/// <param name="IsNumber">Whether it must be a whole number.</param>
public sealed record InputItem(string FieldKey, string Label, long? PlayerId, bool Required, bool IsNumber);

/// <summary>One step of the flow.</summary>
/// <param name="Kind">What it asks.</param>
/// <param name="Title">A short heading.</param>
/// <param name="Required">False for a step the reporter may skip.</param>
/// <param name="FieldKey">The question for a pick step.</param>
/// <param name="Items">The typed answers for a form step (at most five: Discord's limit for one form).</param>
public sealed record ReportStep(ReportStepKind Kind, string Title, bool Required, string? FieldKey = null, IReadOnlyList<InputItem>? Items = null);

/// <summary>A report in progress: the choices so far, and where the reporter is. The steps are built from the
/// game's own question list (required questions first, then the optional ones the reporter can skip), so a new game
/// needs no new code. Held in memory by <see cref="ReportSessionStore"/>; only the person who started it can continue.</summary>
public sealed class ReportSession
{
    /// <summary>Discord allows five inputs in one form.</summary>
    public const int InputsPerForm = 5;

    /// <summary>Selects per page: three, so a page fits Discord's five rows with the page and step buttons.</summary>
    public const int PlayersPerPage = 3;

    /// <summary>The session id (in every component id of the flow).</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The Discord user reporting.</summary>
    public ulong OwnerId { get; init; }

    /// <summary>The Discord guild (server) id.</summary>
    public ulong GuildId { get; init; }

    /// <summary>The table.</summary>
    public Guid TableId { get; init; }

    /// <summary>The table's game, with its questions.</summary>
    public GameSpec Game { get; init; } = null!;

    /// <summary>Who is seated at the table (for the "use the seated players" shortcut).</summary>
    public IReadOnlyList<long> Seated { get; init; } = [];

    /// <summary>The players, in the order chosen.</summary>
    public List<long> Players { get; } = [];

    /// <summary>What the reporter sees for each player (their server display name). Used only in this private flow.</summary>
    public Dictionary<long, string> Labels { get; } = [];

    /// <summary>Finishing places (1 is best), or for a winning-side game the side (1 or 2); for a single-winner game only the winner has one.</summary>
    public Dictionary<long, int> Places { get; } = [];

    /// <summary>Per-player answers: question key, then player.</summary>
    public Dictionary<string, Dictionary<long, string>> PlayerValues { get; } = [];

    /// <summary>Table-wide answers.</summary>
    public Dictionary<string, string> TableValues { get; } = [];

    /// <summary>The reporter said the board photo is posted in the thread.</summary>
    public bool PhotoDone { get; set; }

    /// <summary>The reporter chose to skip the photo (not allowed when the game requires it).</summary>
    public bool PhotoSkipped { get; set; }

    /// <summary>The current step (an index into <see cref="Steps"/>).</summary>
    public int StepIndex { get; set; }

    /// <summary>The current page of selects within a step.</summary>
    public int Page { get; set; }

    /// <summary>When the session is thrown away if nobody finishes it.</summary>
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow + ReportSessionStore.Lifetime;

    /// <summary>The label for a player.</summary>
    /// <param name="userId">The player.</param>
    /// <returns>Their name as the reporter sees it.</returns>
    public string Label(long userId) => Labels.TryGetValue(userId, out var name) ? name : $"player {userId}";

    /// <summary>The steps for the current players. Pick-list questions each get a step and typed answers are
    /// gathered into forms of five; required ones come first, then the optional ones (which can be skipped), then the
    /// photo, then the summary.</summary>
    public IReadOnlyList<ReportStep> Steps => Plan(Game, Players, Label);

    /// <summary>The current step.</summary>
    public ReportStep Current => Steps[Math.Clamp(StepIndex, 0, Steps.Count - 1)];

    /// <summary>Builds the steps for a game and a set of players.</summary>
    /// <param name="game">The game.</param>
    /// <param name="players">The players.</param>
    /// <param name="label">How to show a player.</param>
    /// <returns>The steps, in order.</returns>
    public static IReadOnlyList<ReportStep> Plan(GameSpec game, IReadOnlyList<long> players, Func<long, string> label)
    {
        var steps = new List<ReportStep>
        {
            new(ReportStepKind.Players, "Who played?", true),
            new(ReportStepKind.Places, game.ResultType switch
            {
                GameResultType.SingleWinner => "Who won?",
                GameResultType.WinningSide => "Which side was each player on?",
                _ => "How did everyone finish?",
            }, true),
        };

        var questions = game.Fields.Where(f => f.Kind != GameFieldKind.Player).ToList();
        foreach (var required in new[] { true, false })
        {
            foreach (var field in questions.Where(f => f.Required == required && f.Kind == GameFieldKind.PickList))
            {
                steps.Add(new ReportStep(
                    field.Scope == GameFieldScope.Player ? ReportStepKind.PlayerPick : ReportStepKind.TablePick,
                    field.Label, required, field.Key));
            }

            var items = new List<InputItem>();
            foreach (var field in questions.Where(f => f.Required == required && f.Kind is GameFieldKind.Number or GameFieldKind.Text))
            {
                if (field.Scope == GameFieldScope.Player)
                {
                    items.AddRange(players.Select(p => new InputItem(field.Key, $"{field.Label}: {label(p)}", p, required, field.Kind == GameFieldKind.Number)));
                }
                else
                {
                    items.Add(new InputItem(field.Key, field.Label, null, required, field.Kind == GameFieldKind.Number));
                }
            }

            for (var i = 0; i < items.Count; i += InputsPerForm)
            {
                steps.Add(new ReportStep(ReportStepKind.Inputs, required ? "Scores and details" : "More details (optional)", required,
                    Items: [.. items.Skip(i).Take(InputsPerForm)]));
            }
        }

        steps.Add(new ReportStep(ReportStepKind.Photo, "A photo of the final board", game.PhotoRequired));
        steps.Add(new ReportStep(ReportStepKind.Summary, "Check and submit", true));
        return steps;
    }

    /// <summary>The report as the API takes it.</summary>
    /// <returns>The players and answers gathered so far.</returns>
    public ResultSubmission BuildSubmission()
    {
        var perPlayerKeys = Game.Fields.Where(f => f.Kind != GameFieldKind.Player && f.Scope == GameFieldScope.Player).Select(f => f.Key).ToHashSet();
        var tableKeys = Game.Fields.Where(f => f.Kind != GameFieldKind.Player && f.Scope == GameFieldScope.Table).Select(f => f.Key).ToHashSet();
        var players = Players.Select(id => new SubmittedPlayer(
            id, null, Places.TryGetValue(id, out var place) ? place : null,
            PlayerValues.Where(kv => perPlayerKeys.Contains(kv.Key) && kv.Value.ContainsKey(id)).ToDictionary(kv => kv.Key, kv => kv.Value[id]))).ToList();
        return new ResultSubmission(players, TableValues.Where(kv => tableKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    /// <summary>What is wrong with the current step, if anything, so the reporter is told before moving on.</summary>
    /// <returns>A message for the reporter, or null when the step is complete.</returns>
    public string? ProblemWithCurrentStep()
    {
        var step = Current;
        switch (step.Kind)
        {
            case ReportStepKind.Players:
                return Players.Count < Game.MinSize || Players.Count > Math.Min(Game.MaxSize, 25)
                    ? $"{Game.Name} needs {Game.MinSize} to {Math.Min(Game.MaxSize, 25)} players (you have {Players.Count})."
                    : null;

            case ReportStepKind.Places:
                return PlacesProblem();

            case ReportStepKind.PlayerPick:
                var values = PlayerValues.GetValueOrDefault(step.FieldKey!, []);
                return step.Required && Players.Any(p => !values.ContainsKey(p)) ? $"Pick a {step.Title.ToLowerInvariant()} for everyone." : null;

            case ReportStepKind.TablePick:
                return step.Required && !TableValues.ContainsKey(step.FieldKey!) ? $"Pick the {step.Title.ToLowerInvariant()}." : null;

            case ReportStepKind.Inputs:
                return step.Required && step.Items!.Any(i => i.Required && string.IsNullOrWhiteSpace(ValueOf(i))) ? "Fill in the form before moving on." : null;

            case ReportStepKind.Photo:
                return Game.PhotoRequired && !PhotoDone ? "This game needs the board photo: post it in the thread, then press \"I've posted it\"." : null;

            default:
                return null;
        }
    }

    /// <summary>The answer already given for a typed item.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The answer, or null.</returns>
    public string? ValueOf(InputItem item) =>
        item.PlayerId is { } player
            ? PlayerValues.GetValueOrDefault(item.FieldKey)?.GetValueOrDefault(player)
            : TableValues.GetValueOrDefault(item.FieldKey);

    private string? PlacesProblem()
    {
        switch (Game.ResultType)
        {
            case GameResultType.SingleWinner:
                return Places.Values.Count(p => p == 1) == 1 ? null : "Pick who won.";

            case GameResultType.WinningSide:
                return Players.All(Places.ContainsKey) && Places.Values.Contains(1) && Places.Values.Contains(2)
                    ? null : "Put each player on a side, with at least one on each.";

            default:
                return !Players.All(Places.ContainsKey) ? "Give everyone a finishing place."
                    : !Places.Values.Contains(1) ? "Someone has to have finished first." : null;
        }
    }
}

/// <summary>The reporter's moves, as plain operations on a session: easy to script in a test with no Discord at all, and
/// the single place the rules live (the Discord module only translates interactions into these calls).</summary>
public static class ReportFlow
{
    /// <summary>Whether a game can be reported through Discord: it has a player list, and its tables fit a user-select
    /// menu (25) and every pick-list fits a select menu (25). Bigger games are reported on the website.</summary>
    /// <param name="game">The game.</param>
    /// <returns>True when the Discord flow can handle it.</returns>
    public static bool IsReportable(GameSpec game) =>
        game.Fields.Any(f => f.Kind == GameFieldKind.Player)
        && game.MaxSize <= 25
        && game.Fields.Where(f => f.Kind == GameFieldKind.PickList).All(f => (f.Values?.Count ?? 0) <= 25);

    /// <summary>Starts a report with the seated players already chosen.</summary>
    /// <param name="ownerId">The reporter.</param>
    /// <param name="guildId">The server.</param>
    /// <param name="tableId">The table.</param>
    /// <param name="game">The table's game.</param>
    /// <param name="seated">Who is seated.</param>
    /// <param name="labels">What to call each seated player.</param>
    /// <returns>The session, at the first step.</returns>
    public static ReportSession Start(ulong ownerId, ulong guildId, Guid tableId, GameSpec game, IReadOnlyList<long> seated, IReadOnlyDictionary<long, string> labels)
    {
        var session = new ReportSession { OwnerId = ownerId, GuildId = guildId, TableId = tableId, Game = game, Seated = seated };
        if (seated.Count >= game.MinSize && seated.Count <= Math.Min(game.MaxSize, 25))
        {
            SetPlayers(session, seated, labels);
        }

        return session;
    }

    /// <summary>Chooses who played. Answers for anyone no longer playing are dropped; the labels are remembered.</summary>
    /// <param name="session">The session.</param>
    /// <param name="players">The players.</param>
    /// <param name="labels">What to call them.</param>
    public static void SetPlayers(ReportSession session, IEnumerable<long> players, IReadOnlyDictionary<long, string> labels)
    {
        var chosen = players.Distinct().ToList();
        session.Players.Clear();
        session.Players.AddRange(chosen);
        foreach (var id in chosen)
        {
            if (labels.TryGetValue(id, out var name))
            {
                session.Labels[id] = name;
            }
        }

        foreach (var gone in session.Places.Keys.Where(k => !chosen.Contains(k)).ToList())
        {
            session.Places.Remove(gone);
        }

        foreach (var values in session.PlayerValues.Values)
        {
            foreach (var gone in values.Keys.Where(k => !chosen.Contains(k)).ToList())
            {
                values.Remove(gone);
            }
        }
    }

    /// <summary>Sets a finishing place (or side).</summary>
    /// <param name="session">The session.</param>
    /// <param name="userId">The player.</param>
    /// <param name="place">The place.</param>
    public static void SetPlace(ReportSession session, long userId, int place) => session.Places[userId] = place;

    /// <summary>Sets the winner of a single-winner game (everyone else is cleared).</summary>
    /// <param name="session">The session.</param>
    /// <param name="userId">The winner.</param>
    public static void SetWinner(ReportSession session, long userId)
    {
        session.Places.Clear();
        session.Places[userId] = 1;
    }

    /// <summary>Answers a per-player pick-list question for one player; an empty value clears it.</summary>
    /// <param name="session">The session.</param>
    /// <param name="fieldKey">The question.</param>
    /// <param name="userId">The player.</param>
    /// <param name="value">The choice.</param>
    public static void SetPlayerPick(ReportSession session, string fieldKey, long userId, string? value)
    {
        if (!session.PlayerValues.TryGetValue(fieldKey, out var values))
        {
            values = [];
            session.PlayerValues[fieldKey] = values;
        }

        if (string.IsNullOrEmpty(value))
        {
            values.Remove(userId);
        }
        else
        {
            values[userId] = value;
        }
    }

    /// <summary>Answers a table-wide pick-list question; an empty value clears it.</summary>
    /// <param name="session">The session.</param>
    /// <param name="fieldKey">The question.</param>
    /// <param name="value">The choice.</param>
    public static void SetTablePick(ReportSession session, string fieldKey, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            session.TableValues.Remove(fieldKey);
        }
        else
        {
            session.TableValues[fieldKey] = value;
        }
    }

    /// <summary>Applies the answers typed into a form. Numbers must be whole; anything wrong is reported and nothing from
    /// that form is kept, so the reporter can try again.</summary>
    /// <param name="session">The session.</param>
    /// <param name="step">The form's step.</param>
    /// <param name="answers">What was typed, by the item's position in the form (0 to 4).</param>
    /// <returns>A message for the reporter when something is wrong; null when it was all kept.</returns>
    public static string? ApplyInputs(ReportSession session, ReportStep step, IReadOnlyDictionary<int, string> answers)
    {
        var items = step.Items ?? [];
        var accepted = new List<(InputItem Item, string Value)>();
        for (var i = 0; i < items.Count; i++)
        {
            var typed = answers.GetValueOrDefault(i)?.Trim() ?? "";
            var item = items[i];
            if (typed.Length == 0)
            {
                if (item.Required)
                {
                    return $"{item.Label} is required.";
                }

                continue;
            }

            if (item.IsNumber && !long.TryParse(typed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            {
                return $"{item.Label} must be a whole number.";
            }

            if (typed.Length > GameRules.TextAnswerLength)
            {
                return $"{item.Label} is too long.";
            }

            accepted.Add((item, typed));
        }

        foreach (var (item, value) in accepted)
        {
            if (item.PlayerId is { } player)
            {
                SetPlayerPick(session, item.FieldKey, player, value);
            }
            else
            {
                SetTablePick(session, item.FieldKey, value);
            }
        }

        return null;
    }

    /// <summary>Says the board photo is posted.</summary>
    /// <param name="session">The session.</param>
    public static void AcknowledgePhoto(ReportSession session)
    {
        session.PhotoDone = true;
        session.PhotoSkipped = false;
    }

    /// <summary>Skips the photo, unless the game requires it.</summary>
    /// <param name="session">The session.</param>
    /// <returns>A message for the reporter when the photo cannot be skipped; null when skipped.</returns>
    public static string? SkipPhoto(ReportSession session)
    {
        if (session.Game.PhotoRequired)
        {
            return "This game needs the board photo: post it in the thread, then press \"I've posted it\".";
        }

        session.PhotoSkipped = true;
        session.PhotoDone = false;
        return null;
    }

    /// <summary>Moves on if the current step is complete.</summary>
    /// <param name="session">The session.</param>
    /// <returns>A message for the reporter when something is missing; null when it moved on.</returns>
    public static string? Next(ReportSession session)
    {
        if (session.ProblemWithCurrentStep() is { } problem)
        {
            return problem;
        }

        Move(session, +1);
        return null;
    }

    /// <summary>Moves back a step.</summary>
    /// <param name="session">The session.</param>
    public static void Back(ReportSession session) => Move(session, -1);

    /// <summary>Skips an optional step.</summary>
    /// <param name="session">The session.</param>
    /// <returns>A message when the step cannot be skipped; null when it was.</returns>
    public static string? Skip(ReportSession session)
    {
        if (session.Current.Kind == ReportStepKind.Photo)
        {
            var refused = SkipPhoto(session); // refused, with its own words, when the game requires the photo
            if (refused is null)
            {
                Move(session, +1);
            }

            return refused;
        }

        if (session.Current.Required)
        {
            return "That one can't be skipped.";
        }

        Move(session, +1);
        return null;
    }

    /// <summary>Goes back to the finishing places to correct something, keeping every answer given.</summary>
    /// <param name="session">The session.</param>
    public static void Edit(ReportSession session)
    {
        session.StepIndex = 1;
        session.Page = 0;
    }

    /// <summary>Everything the API's checks would object to, in words for the summary.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The problems; empty when the report can be submitted.</returns>
    public static IReadOnlyList<string> Problems(ReportSession session)
    {
        var problems = ResultValidator.Validate(session.Game, session.BuildSubmission()).Select(p => p.Message).ToList();
        if (session.Game.PhotoRequired && !session.PhotoDone)
        {
            problems.Add("The board photo is required: post it in the thread, then press \"I've posted it\".");
        }

        return problems;
    }

    /// <summary>The request to send, once <see cref="Problems"/> is empty.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The submission with the reporter named (the bot says who is reporting).</returns>
    public static SubmitResultRequest ToRequest(ReportSession session) =>
        new(session.BuildSubmission(), ReporterUserId: (long)session.OwnerId);

    private static void Move(ReportSession session, int by)
    {
        session.StepIndex = Math.Clamp(session.StepIndex + by, 0, session.Steps.Count - 1);
        session.Page = 0;
    }
}

/// <summary>Holds reports in progress for 20 minutes. In memory: the bot is one process, and a report lost to a restart
/// just means pressing Report game again.</summary>
public sealed class ReportSessionStore
{
    /// <summary>How long an unfinished report lives.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);

    private readonly ConcurrentDictionary<Guid, ReportSession> sessions = new();

    /// <summary>Remembers a session (and forgets any that have expired).</summary>
    /// <param name="session">The session.</param>
    public void Add(ReportSession session)
    {
        foreach (var (id, old) in sessions)
        {
            if (old.ExpiresAt < DateTimeOffset.UtcNow)
            {
                sessions.TryRemove(id, out _);
            }
        }

        sessions[session.Id] = session;
    }

    /// <summary>Finds a live session that belongs to the caller and gives it a fresh lifetime.</summary>
    /// <param name="id">The session id.</param>
    /// <param name="userId">The user pressing the component.</param>
    /// <returns>The session, or null when it is unknown, expired or someone else's.</returns>
    public ReportSession? Get(Guid id, ulong userId)
    {
        if (!sessions.TryGetValue(id, out var session) || session.OwnerId != userId || session.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return null;
        }

        session.ExpiresAt = DateTimeOffset.UtcNow + Lifetime;
        return session;
    }

    /// <summary>Forgets a session.</summary>
    /// <param name="id">The session id.</param>
    public void Remove(Guid id) => sessions.TryRemove(id, out _);
}
