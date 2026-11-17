namespace CalCrony.Contracts.GameNights;

/// <summary>What a member says about a game for one night. One answer per game per person; changing it
/// replaces the old one. Stored and sent as the number.</summary>
public enum SignalKind
{
    /// <summary>I know it well enough to teach it.</summary>
    CanTeach = 0,

    /// <summary>I'd like to play it, and I already know how.</summary>
    CanPlay = 1,

    /// <summary>I'd like to play it and I'm new to it, so I'll need teaching.</summary>
    NewToIt = 2,

    /// <summary>Not this one tonight.</summary>
    NotTonight = 3,
}

/// <summary>One answer.</summary>
/// <param name="GameId">The game.</param>
/// <param name="Kind">The answer.</param>
public record SignalDto(Guid GameId, SignalKind Kind);

/// <summary>A person's answers for a night.</summary>
/// <param name="Signals">Their answers, one per game they have answered.</param>
public record MySignalsDto(IReadOnlyList<SignalDto> Signals);

/// <summary>Sets one answer. The bot says whose it is; the website always means the signed-in member.</summary>
/// <param name="Kind">The answer; null clears it.</param>
/// <param name="UserId">The member (bot callers only).</param>
public record SetSignalRequest(SignalKind? Kind, long? UserId = null);

/// <summary>Replaces all of a member's answers for a night.</summary>
/// <param name="Signals">The full set; games not listed are cleared.</param>
/// <param name="UserId">The member (bot callers only).</param>
public record SetMySignalsRequest(IReadOnlyList<SignalDto> Signals, long? UserId = null);

/// <summary>One member's answer for one game (for organisers).</summary>
/// <param name="UserId">The member.</param>
/// <param name="GameId">The game.</param>
/// <param name="Kind">The answer.</param>
public record PersonSignalDto(long UserId, Guid GameId, SignalKind Kind);

/// <summary>How many people said what about one game.</summary>
/// <param name="GameId">The game.</param>
/// <param name="GameName">Its name.</param>
/// <param name="CanTeach">People who can teach it.</param>
/// <param name="CanPlay">People who know it and want to play.</param>
/// <param name="NewToIt">People who are new to it and want to play.</param>
/// <param name="NotTonight">People who passed on it.</param>
public record GameDemandDto(Guid GameId, string GameName, int CanTeach, int CanPlay, int NewToIt, int NotTonight)
{
    /// <summary>People who want to play it (teachers count: they play too).</summary>
    public int Interested => CanTeach + CanPlay + NewToIt;

    /// <summary>Whether anyone can teach it.</summary>
    public bool HasTeacher => CanTeach > 0;

    /// <summary>True when people want to play it and nobody can teach it: the organisers' cue.</summary>
    public bool NeedsTeacher => Interested > 0 && !HasTeacher;
}

/// <summary>What a night's answers add up to.</summary>
/// <param name="Games">Demand per game, busiest first; every active game appears, even with no answers.</param>
/// <param name="Teachers">People who can teach at least one game.</param>
/// <param name="Experienced">People who can teach or play at least one game they know.</param>
/// <param name="Fresh">People who are new to at least one game they want to play.</param>
/// <param name="Respondents">People who have answered anything.</param>
public record NightSignalsSummary(IReadOnlyList<GameDemandDto> Games, int Teachers, int Experienced, int Fresh, int Respondents);

/// <summary>Who can teach a game.</summary>
/// <param name="GameId">The game.</param>
/// <param name="GameName">Its name.</param>
/// <param name="Teachers">The members who said they can teach it.</param>
public record TeacherRosterEntryDto(Guid GameId, string GameName, IReadOnlyList<long> Teachers);

/// <summary>The arithmetic behind the demand panel, the event post and the roster. Pure, so it is pinned by tests.</summary>
public static class SignalMath
{
    /// <summary>Adds up a night's answers.</summary>
    /// <param name="games">The active games (id and name).</param>
    /// <param name="signals">Every answer for the night. Answers for games not in <paramref name="games"/> are ignored.</param>
    /// <returns>The summary: games busiest first (then by name), and the people counts.</returns>
    public static NightSignalsSummary Summarize(
        IEnumerable<(Guid Id, string Name)> games, IEnumerable<PersonSignalDto> signals)
    {
        var known = games.ToList();
        var ids = known.Select(g => g.Id).ToHashSet();
        var relevant = signals.Where(s => ids.Contains(s.GameId)).ToList();

        var demand = known
            .Select(g => new GameDemandDto(
                g.Id, g.Name,
                relevant.Count(s => s.GameId == g.Id && s.Kind == SignalKind.CanTeach),
                relevant.Count(s => s.GameId == g.Id && s.Kind == SignalKind.CanPlay),
                relevant.Count(s => s.GameId == g.Id && s.Kind == SignalKind.NewToIt),
                relevant.Count(s => s.GameId == g.Id && s.Kind == SignalKind.NotTonight)))
            .OrderByDescending(d => d.Interested)
            .ThenBy(d => d.GameName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int People(Func<PersonSignalDto, bool> filter) => relevant.Where(filter).Select(s => s.UserId).Distinct().Count();
        return new NightSignalsSummary(
            demand,
            Teachers: People(s => s.Kind == SignalKind.CanTeach),
            Experienced: People(s => s.Kind is SignalKind.CanTeach or SignalKind.CanPlay),
            Fresh: People(s => s.Kind == SignalKind.NewToIt),
            Respondents: People(_ => true));
    }

    /// <summary>Who can teach each game.</summary>
    /// <param name="games">The active games.</param>
    /// <param name="signals">Every answer for the night.</param>
    /// <returns>One entry per game that has at least one teacher, games in name order and teachers in id order.</returns>
    public static IReadOnlyList<TeacherRosterEntryDto> Roster(
        IEnumerable<(Guid Id, string Name)> games, IEnumerable<PersonSignalDto> signals)
    {
        var teachers = signals.Where(s => s.Kind == SignalKind.CanTeach).ToLookup(s => s.GameId, s => s.UserId);
        return [.. games
            .Where(g => teachers.Contains(g.Id))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TeacherRosterEntryDto(g.Id, g.Name, [.. teachers[g.Id].Distinct().Order()]))];
    }
}
