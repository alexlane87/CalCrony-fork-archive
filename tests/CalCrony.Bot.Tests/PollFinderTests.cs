using CalCrony.Bot.Api;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

public class PollFinderTests
{
    [Fact]
    public void Exact_question_wins_over_longer_questions_containing_it()
    {
        // Live repro: "Session 0" was unreachable next to "What evenings can you do for Session 0?".
        var polls = new List<PollDto> { Sample("Session 0"), Sample("What evenings can you do for Session 0?") };

        var (poll, problem) = PollFinder.PickSingle(polls, "Session 0");

        Assert.Null(problem);
        Assert.Equal("Session 0", poll!.Question);
    }

    [Fact]
    public void Exact_match_ignores_case_and_surrounding_quotes()
    {
        var polls = new List<PollDto> { Sample("Session 0"), Sample("What evenings can you do for Session 0?") };

        var (poll, problem) = PollFinder.PickSingle(polls, "\"session 0\"");

        Assert.Null(problem);
        Assert.Equal("Session 0", poll!.Question);
    }

    [Fact]
    public void Smart_quotes_are_ignored_too()
    {
        var polls = new List<PollDto> { Sample("Session 0") };

        var (poll, problem) = PollFinder.PickSingle(polls, "“Session 0”");

        Assert.Null(problem);
        Assert.NotNull(poll);
    }

    [Fact]
    public void A_fragment_unique_to_one_poll_still_matches()
    {
        var polls = new List<PollDto> { Sample("Session 0"), Sample("What evenings can you do for Session 0?") };

        var (poll, problem) = PollFinder.PickSingle(polls, "What evenings");

        Assert.Null(problem);
        Assert.StartsWith("What evenings", poll!.Question);
    }

    [Fact]
    public void Ambiguous_fragments_without_exact_match_still_list_candidates()
    {
        var polls = new List<PollDto> { Sample("Raid night A"), Sample("Raid night B") };

        var (poll, problem) = PollFinder.PickSingle(polls, "Raid");

        Assert.Null(poll);
        Assert.Contains("Raid night A", problem);
        Assert.Contains("Raid night B", problem);
    }

    [Fact]
    public void Duplicate_exact_questions_stay_ambiguous()
    {
        var polls = new List<PollDto> { Sample("Session 0"), Sample("Session 0") };

        var (poll, problem) = PollFinder.PickSingle(polls, "Session 0");

        Assert.Null(poll);
        Assert.Contains("Multiple polls", problem);
    }

    [Fact]
    public void No_match_names_the_status_asked_for()
    {
        var (poll, problem) = PollFinder.PickSingle([], "Session 0", PollStatus.Open);

        Assert.Null(poll);
        Assert.Equal("No open poll matching \"Session 0\".", problem);
    }

    private static PollDto Sample(string question) => new(
        Guid.NewGuid(), 1, 2, question, IsTimePoll: false, SingleVote: true, Anonymous: false,
        AllowUserOptions: false, ChannelId: 3, MessageId: null, PollStatus.Open, ClosesAtUtc: null,
        ClosedAtUtc: null, TimeZone: "UTC", ConvertedEventId: null, Options: [], Votes: []);
}
