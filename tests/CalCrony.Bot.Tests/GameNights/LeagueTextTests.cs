using CalCrony.Bot.GameNights;
using CalCrony.Contracts.GameNights;

namespace CalCrony.Bot.Tests.GameNights;

public class LeagueTextTests
{
    private static StandingDto Row(int rank, string name, int rating, int games) => new(rank, rank, null, name, rating, rating, games);

    private static LeagueStandingsDto Table(IReadOnlyList<StandingDto> rows, string scope = "All time", int counted = 3) =>
        new(Guid.NewGuid(), "Root", null, scope, rows, [], counted);

    [Fact]
    public void A_table_shows_rank_name_rating_and_games_with_the_scope_and_the_number_of_games_counted()
    {
        var embed = LeagueText.Standings(Table([Row(1, "Alex P", 1012, 3), Row(2, "Bea W", 988, 1)], scope: "Winter 2026"));

        Assert.Equal("🏆 Root: Winter 2026", embed.Title);
        Assert.Contains("` 1.` **Alex P** · 1012 · 3 games", embed.Description);
        Assert.Contains("` 2.` **Bea W** · 988 · 1 game", embed.Description);
        Assert.DoesNotContain("1 games", embed.Description);
        Assert.Contains("3 confirmed games counted", embed.Footer!.Value.Text);
        Assert.Contains("only public tables count", embed.Footer.Value.Text);
    }

    [Fact]
    public void Players_on_the_same_rating_share_a_rank()
    {
        var embed = LeagueText.Standings(Table([Row(1, "Alex P", 1000, 2), Row(1, "Bea W", 1000, 2), Row(3, "Cal T", 990, 2)]));

        Assert.Contains("` 1.` **Alex P**", embed.Description);
        Assert.Contains("` 1.` **Bea W**", embed.Description);
        Assert.Contains("` 3.` **Cal T**", embed.Description);
    }

    [Fact]
    public void An_empty_table_explains_how_players_join_and_a_long_one_is_cut_to_the_top_with_a_count()
    {
        Assert.Contains("Players join the league by playing", LeagueText.Standings(Table([])).Description);

        var many = Enumerable.Range(1, 40).Select(i => Row(i, $"Player {i}", 1000 - i, 1)).ToList();
        var embed = LeagueText.Standings(Table(many));
        Assert.Contains("…and 25 more", embed.Description);
        Assert.DoesNotContain("Player 16", embed.Description);
        Assert.InRange(embed.Description.Length, 1, 4096);
    }

    [Fact]
    public void My_ratings_list_each_game_with_rank_and_say_how_to_get_one_when_there_are_none()
    {
        Assert.Contains("You don't have a league rating yet", LeagueText.Mine([]));

        var text = LeagueText.Mine([new MyRatingDto(Guid.NewGuid(), "Root", "All time", 1012, 1, 8, 3), new MyRatingDto(Guid.NewGuid(), "Arcs", "Winter", 990, 4, 6, 1)]);

        Assert.Contains("**Root** (All time): **1012**, #1 of 8, 3 games", text);
        Assert.Contains("**Arcs** (Winter): **990**, #4 of 6, 1 game", text);
    }
}
