using System.Text.RegularExpressions;

namespace CalCrony.Contracts.GameNights;

/// <summary>Where a reported result stands. Stored and sent as the number.</summary>
public enum ResultStatus
{
    /// <summary>Reported; waiting for the other players to confirm.</summary>
    Pending = 0,

    /// <summary>Confirmed by the table (or by a moderator). Only confirmed results feed leagues.</summary>
    Confirmed = 1,

    /// <summary>A player rejected it; organisers have been told.</summary>
    Disputed = 2,

    /// <summary>Withdrawn by a moderator. The table can report again.</summary>
    Voided = 3,
}

/// <summary>A player's answer to a reported result.</summary>
public enum ConfirmationDecision
{
    /// <summary>That's what happened.</summary>
    Confirm = 0,

    /// <summary>That's wrong.</summary>
    Reject = 1,
}

/// <summary>How many of the table must confirm a result. Configured as <c>GameNights:ConfirmMode</c>.</summary>
public enum ConfirmMode
{
    /// <summary>Every member who played (the reporter counts as confirming).</summary>
    All = 0,

    /// <summary>More than half of the members who played.</summary>
    Majority = 1,
}

/// <summary>What a moderator can do to a result.</summary>
public enum ResolveAction
{
    /// <summary>Accept it as it stands (settles a dispute, or confirms one nobody answered).</summary>
    Confirm = 0,

    /// <summary>Withdraw it so the table can report again.</summary>
    Void = 1,
}

/// <summary>One player in a stored result.</summary>
/// <param name="UserId">The member's Discord id, for a member.</param>
/// <param name="GuestId">The guest's id, for a guest.</param>
/// <param name="DisplayName">"First L", "Former member", "Unnamed player" or a guest's typed name.</param>
/// <param name="Placement">Finishing place (1 is best).</param>
/// <param name="Score">The final score, when the game asks for one.</param>
/// <param name="Values">The other per-player answers, by question key.</param>
public record ResultPlayerDto(
    long? UserId, Guid? GuestId, string DisplayName, int? Placement, long? Score, IReadOnlyDictionary<string, string> Values);

/// <summary>One member's answer to a result.</summary>
/// <param name="UserId">The member.</param>
/// <param name="Decision">Their answer.</param>
public record ResultConfirmationDto(long UserId, ConfirmationDecision Decision);

/// <summary>A reported result.</summary>
/// <param name="Id">The result id.</param>
/// <param name="TableId">The table.</param>
/// <param name="EventId">The game night.</param>
/// <param name="GameId">The game.</param>
/// <param name="GameName">The game's name.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="ReporterUserId">Who reported it.</param>
/// <param name="SubmittedAtUtc">When.</param>
/// <param name="ConfirmedAtUtc">When it was confirmed, if it has been.</param>
/// <param name="Players">Who played and how it went.</param>
/// <param name="TableValues">The table-wide answers (map, deck, round the game ended...).</param>
/// <param name="Confirmations">Who has answered.</param>
/// <param name="AwaitingUserIds">The members whose answer is still needed (while pending).</param>
/// <param name="UnnamedUserIds">Members the bot has no usable "First L" name for; the bot nudges them to fix their nickname.</param>
public record ResultDto(
    Guid Id, Guid TableId, Guid EventId, Guid GameId, string GameName, ResultStatus Status, long ReporterUserId,
    DateTimeOffset SubmittedAtUtc, DateTimeOffset? ConfirmedAtUtc, IReadOnlyList<ResultPlayerDto> Players,
    IReadOnlyDictionary<string, string> TableValues, IReadOnlyList<ResultConfirmationDto> Confirmations,
    IReadOnlyList<long> AwaitingUserIds, IReadOnlyList<long> UnnamedUserIds, long? SummaryMessageId = null);

/// <summary>Reports a result for a table. No photo is part of this: the board photo stays in Discord.</summary>
/// <param name="Submission">Who played and the answers; checked by <see cref="ResultValidator"/>.</param>
/// <param name="ReporterUserId">The reporter (the bot says; the website means the signed-in member).</param>
/// <param name="NameOverrides">One-off "First L" names for members the bot has no usable name for. Used to word
/// this response only; never stored against the member.</param>
public record SubmitResultRequest(
    ResultSubmission Submission, long? ReporterUserId = null, IReadOnlyDictionary<long, string>? NameOverrides = null);

/// <summary>Confirms or rejects a result as a player.</summary>
/// <param name="UserId">The member (bot callers only; the website means the signed-in member).</param>
public record AnswerResultRequest(long? UserId = null);

/// <summary>A moderator settles a result.</summary>
/// <param name="Action">Confirm it or void it.</param>
public record ResolveResultRequest(ResolveAction Action);

/// <summary>A guest: someone with no Discord account who played. The typed "First L" name is the only
/// personal detail kept.</summary>
/// <param name="Id">The guest id.</param>
/// <param name="DisplayName">The typed name, or "Former guest" once erased.</param>
public record GuestDto(Guid Id, string DisplayName);

/// <summary>Adds a guest.</summary>
/// <param name="DisplayName">The guest's name as "First L".</param>
public record CreateGuestRequest(string DisplayName);

/// <summary>A member's "First L" name as the bot worked it out from their server nickname.</summary>
/// <param name="UserId">The member.</param>
/// <param name="DisplayName">"First L", or null when the nickname does not follow the convention.</param>
/// <param name="Left">True when the member is no longer in the server.</param>
public record MemberNameDto(long UserId, string? DisplayName, bool Left = false);

/// <summary>The bot's batch of names.</summary>
/// <param name="Members">The names.</param>
public record PutMemberNamesRequest(IReadOnlyList<MemberNameDto> Members);

/// <summary>Members whose names the bot should look up now (they appear at tables or in results and the
/// API's copy is missing or stale).</summary>
/// <param name="UserIds">The members.</param>
public record WantedNamesResponse(IReadOnlyList<long> UserIds);

/// <summary>Outbox payload: a result was rejected; tell the organisers.</summary>
/// <param name="ResultId">The result.</param>
/// <param name="TableId">The table.</param>
/// <param name="GuildId">The Discord guild (server) id.</param>
/// <param name="RejectedByUserId">Who rejected it.</param>
/// <param name="NotifyRoleIds">The roles mapped to organiser, moderator or admin, to mention.</param>
public record ResultDisputedPayload(Guid ResultId, Guid TableId, long GuildId, long RejectedByUserId, IReadOnlyList<long> NotifyRoleIds);

/// <summary>Outbox payload for the result posts: submitted, reminder, settled. The bot reads the result from the API, so
/// the notice is safe to repeat and always describes the result as it is now.</summary>
/// <param name="ResultId">The result.</param>
/// <param name="GuildId">The Discord guild (server) id.</param>
/// <param name="TableId">The table.</param>
public record ResultNoticePayload(Guid ResultId, long GuildId, Guid TableId);

/// <summary>Records the Discord message the bot posted for a result (bot only), so it can be updated when the result
/// settles.</summary>
/// <param name="MessageId">The message id, or null to forget it.</param>
public record SetResultMessageRequest(long? MessageId);

/// <summary>The "First L" convention for how people are shown. A name is read from a server nickname by the bot;
/// the API only ever stores this derived form.</summary>
public static partial class DisplayNames
{
    /// <summary>What shows for a member who has left the server.</summary>
    public const string FormerMember = "Former member";

    /// <summary>What shows for a member with no usable name.</summary>
    public const string UnnamedPlayer = "Unnamed player";

    /// <summary>What shows for a guest whose name has been erased.</summary>
    public const string FormerGuest = "Former guest";

    /// <summary>Longest stored name.</summary>
    public const int MaxLength = 40;

    // A first name, then one or more further words made only of letters (hyphen, apostrophe and a trailing dot
    // allowed). Anything else in the nickname (digits, emoji, a pipe and a place name) means it does not follow the
    // convention, and the bot says so instead of guessing.
    [GeneratedRegex(@"^\s*(\p{L}[\p{L}'’\-]*)(?:\s+\p{L}[\p{L}'’\-\.]*)*\s+(\p{L})[\p{L}'’\-\.]*\s*$")]
    private static partial Regex Pattern();

    /// <summary>Works out "First L" from a nickname: the first word with a capital, a space and the capital initial
    /// of the last word. Case does not matter ("alex pearson" becomes "Alex P"). A single word, or anything with
    /// digits, symbols or emoji, does not match.</summary>
    /// <param name="nickname">The server nickname.</param>
    /// <returns>"First L", or null when the nickname does not follow the convention.</returns>
    public static string? FromNickname(string? nickname)
    {
        if (string.IsNullOrWhiteSpace(nickname))
        {
            return null;
        }

        var match = Pattern().Match(nickname);
        if (!match.Success)
        {
            return null;
        }

        var first = match.Groups[1].Value;
        var name = $"{Capitalise(first)} {match.Groups[2].Value.ToUpperInvariant()}";
        return name.Length <= MaxLength ? name : null;
    }

    /// <summary>Whether a typed name is exactly in "First L" form once tidied (so a guest's name or a one-off
    /// override is acceptable).</summary>
    /// <param name="name">The typed name.</param>
    /// <returns>The tidied name, or null when it does not follow the convention.</returns>
    public static string? Tidy(string? name)
    {
        var derived = FromNickname(name);
        if (derived is null)
        {
            return null;
        }

        // Only accept two words (First and an initial or surname): "Anne de Smith" is a nickname we can read,
        // but a typed guest name should be given as the convention asks.
        var words = name!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 2 ? derived : null;
    }

    private static string Capitalise(string word)
    {
        // Mary-Jane, O'Neil: capital after each hyphen or apostrophe, the rest as typed in lower case.
        var chars = word.ToLowerInvariant().ToCharArray();
        var upper = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (upper && char.IsLetter(chars[i]))
            {
                chars[i] = char.ToUpperInvariant(chars[i]);
            }

            upper = chars[i] is '-' or '\'' or '’';
        }

        return new string(chars);
    }
}

/// <summary>The rule for when a reported result counts as confirmed. Pure and shared.</summary>
public static class ResultRules
{
    /// <summary>Whether enough of the table has confirmed.</summary>
    /// <param name="mode">All members, or more than half.</param>
    /// <param name="memberPlayerIds">The members who played (guests cannot confirm, so they are not counted).</param>
    /// <param name="confirmedIds">The members who have confirmed. The reporter is included when they played.</param>
    /// <returns>True when the result is confirmed. A table with no members at all (only guests) is confirmed.</returns>
    public static bool IsConfirmed(ConfirmMode mode, IReadOnlyCollection<long> memberPlayerIds, IReadOnlyCollection<long> confirmedIds)
    {
        var members = memberPlayerIds.Distinct().ToList();
        if (members.Count == 0)
        {
            return true;
        }

        var confirmed = members.Count(confirmedIds.Contains);
        return mode == ConfirmMode.All ? confirmed == members.Count : confirmed * 2 > members.Count;
    }
}
