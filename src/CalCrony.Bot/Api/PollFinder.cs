using CalCrony.Contracts;

namespace CalCrony.Bot.Api;

/// <summary>Resolves user-typed poll references (question fragments) to a single poll, with
/// friendly ambiguity messages.</summary>
public static class PollFinder
{
    /// <summary>Finds exactly one poll whose question matches <paramref name="name"/>
    /// (case-insensitive fragment; a single exact match wins over longer questions that merely
    /// contain it), optionally filtered by status.</summary>
    public static async Task<(PollDto? Poll, string? Problem)> FindSingleAsync(
        CalCronyApiClient api, long guildId, string name, PollStatus? status = null)
    {
        var result = await api.ListPollsAsync(guildId, status, limit: 25);
        if (!result.Success || result.Value is null)
        {
            return (null, $"❌ {result.Error}");
        }

        return PickSingle(result.Value, name, status);
    }

    /// <summary>Pure match logic, public for direct testing.</summary>
    /// <param name="polls">The candidate polls.</param>
    /// <param name="name">What the user typed; surrounding quotes and spaces are ignored.</param>
    /// <param name="status">The status the caller asked for, used only in the not-found message.</param>
    /// <returns>The single poll, or null with a message saying why not.</returns>
    public static (PollDto? Poll, string? Problem) PickSingle(
        IReadOnlyList<PollDto> polls, string name, PollStatus? status = null)
    {
        // People copy the question with quotes around it, which would otherwise match nothing.
        name = name.Trim().Trim('"', '“', '”').Trim();

        var matches = polls
            .Where(p => p.Question.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count > 1)
        {
            // "Session 0" must stay reachable next to "What evenings can you do for Session 0?".
            var exact = matches
                .Where(p => p.Question.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exact.Count == 1)
            {
                return (exact[0], null);
            }
        }

        return matches.Count switch
        {
            0 => (null, $"No {(status is null ? "" : $"{status.ToString()!.ToLowerInvariant()} ")}poll matching \"{name}\"."),
            1 => (matches[0], null),
            _ => (null, $"Multiple polls match \"{name}\": {string.Join(", ", matches.Select(m => $"**{m.Question}**"))}. Be more specific."),
        };
    }
}
