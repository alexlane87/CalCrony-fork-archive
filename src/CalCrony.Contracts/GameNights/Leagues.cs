namespace CalCrony.Contracts.GameNights;

/// <summary>Defaults for ratings (configurable per game).</summary>
public static class RatingDefaults
{
    /// <summary>The rating a new player starts at.</summary>
    public const int Start = 1000;

    /// <summary>How far one game can move a rating.</summary>
    public const int K = 32;
}

/// <summary>One player in a game being rated.</summary>
/// <param name="Key">Who they are (any stable text; the scheme only hands it back).</param>
/// <param name="Rating">Their rating going in.</param>
/// <param name="Placement">Where they finished (1 is best; equal placements tied).</param>
public readonly record struct RatedPlayer(string Key, double Rating, int Placement);

/// <summary>What a game did to one player's rating.</summary>
/// <param name="Key">The player.</param>
/// <param name="Before">Their rating going in.</param>
/// <param name="After">Their rating coming out.</param>
public readonly record struct RatingChange(string Key, double Before, double After)
{
    /// <summary>How much it moved.</summary>
    public double Delta => After - Before;
}

/// <summary>How a finished game changes ratings. An interface so the scheme can be swapped later without touching leagues.</summary>
public interface IRatingScheme
{
    /// <summary>Works out every player's new rating for one game.</summary>
    /// <param name="players">Everyone in the game (two or more).</param>
    /// <param name="k">The K-factor for the game.</param>
    /// <returns>One change per player, in the same order.</returns>
    IReadOnlyList<RatingChange> Apply(IReadOnlyList<RatedPlayer> players, double k);
}

/// <summary>Pairwise multi-player Elo. Each player is compared with every other player in the game:
/// <c>E(i,j) = 1 / (1 + 10^((Rj - Ri) / 400))</c>, the score <c>S(i,j)</c> is 1 for finishing ahead, 0.5 for a tie and 0 for
/// finishing behind, and <c>ΔRi = K / (N - 1) × Σ(S(i,j) - E(i,j))</c> over the other players. With two players this is
/// ordinary Elo; because <c>S(i,j) + S(j,i) = 1</c> and <c>E(i,j) + E(j,i) = 1</c> the changes always add up to zero, so a game
/// never creates or destroys rating.
/// <para>Games that record only a winner rate the winner against everyone and treat the rest as tied with each other
/// (placement 2), so they neither gain nor lose from one another; see <see cref="RatingPlacements"/>.</para></summary>
public sealed class PairwiseElo : IRatingScheme
{
    /// <inheritdoc />
    public IReadOnlyList<RatingChange> Apply(IReadOnlyList<RatedPlayer> players, double k)
    {
        var n = players.Count;
        if (n < 2)
        {
            return [.. players.Select(p => new RatingChange(p.Key, p.Rating, p.Rating))];
        }

        var changes = new List<RatingChange>(n);
        for (var i = 0; i < n; i++)
        {
            double sum = 0;
            for (var j = 0; j < n; j++)
            {
                if (i == j)
                {
                    continue;
                }

                var expected = 1.0 / (1.0 + Math.Pow(10, (players[j].Rating - players[i].Rating) / 400.0));
                var actual = players[i].Placement < players[j].Placement ? 1.0 : players[i].Placement == players[j].Placement ? 0.5 : 0.0;
                sum += actual - expected;
            }

            changes.Add(new RatingChange(players[i].Key, players[i].Rating, players[i].Rating + k / (n - 1) * sum));
        }

        return changes;
    }
}

/// <summary>Turns what a result stores into the placements the rating scheme compares.</summary>
public static class RatingPlacements
{
    /// <summary>The placement to rate each player at.</summary>
    /// <param name="type">How the game records its result.</param>
    /// <param name="placements">What the result stored, by player (null for a player with none).</param>
    /// <returns>A placement for every player: as stored for ranked and winning-side games; for a single-winner game 1 for the
    /// winner and 2 for everyone else (so the others tie each other and are not rated against one another).</returns>
    public static IReadOnlyList<int> For(GameResultType type, IReadOnlyList<int?> placements) => type switch
    {
        GameResultType.SingleWinner => [.. placements.Select(p => p == 1 ? 1 : 2)],
        _ => [.. placements.Select(p => p ?? int.MaxValue)],
    };

    /// <summary>A rating as shown: rounded to a whole number.</summary>
    /// <param name="rating">The exact rating.</param>
    /// <returns>The whole number shown.</returns>
    public static int Display(double rating) => (int)Math.Round(rating, MidpointRounding.AwayFromZero);
}

/// <summary>One line of a league table.</summary>
/// <param name="Rank">Position, 1 is top; players on the same displayed rating share a rank.</param>
/// <param name="UserId">The member, for a member.</param>
/// <param name="GuestId">The guest, for a guest.</param>
/// <param name="DisplayName">"First L", "Former member", "Unnamed player" or a guest's typed name.</param>
/// <param name="Rating">The rating as shown (whole number).</param>
/// <param name="RatingExact">The exact rating.</param>
/// <param name="GamesPlayed">Confirmed games counted.</param>
public record StandingDto(int Rank, long? UserId, Guid? GuestId, string DisplayName, int Rating, double RatingExact, int GamesPlayed);

/// <summary>A season of a league.</summary>
/// <param name="Id">The season id.</param>
/// <param name="Name">Its name.</param>
/// <param name="StartsAtUtc">When it starts.</param>
/// <param name="EndsAtUtc">When it ends; null while it is still running.</param>
public record SeasonDto(Guid Id, string Name, DateTimeOffset StartsAtUtc, DateTimeOffset? EndsAtUtc);

/// <summary>A league table.</summary>
/// <param name="GameId">The game.</param>
/// <param name="GameName">Its name.</param>
/// <param name="SeasonId">The season shown, or null for the all-time table.</param>
/// <param name="Scope">"All time" or the season's name.</param>
/// <param name="Rows">The standings, best first.</param>
/// <param name="Seasons">The league's seasons, newest first.</param>
/// <param name="GamesCounted">How many confirmed results the table is built from.</param>
public record LeagueStandingsDto(
    Guid GameId, string GameName, Guid? SeasonId, string Scope, IReadOnlyList<StandingDto> Rows,
    IReadOnlyList<SeasonDto> Seasons, int GamesCounted);

/// <summary>Starts a season. Starting one ends the running season at that moment.</summary>
/// <param name="Name">The season's name.</param>
/// <param name="StartsAtUtc">When it starts; null means now.</param>
/// <param name="EndsAtUtc">When it ends; null leaves it running until the next season or until ended.</param>
public record CreateSeasonRequest(string Name, DateTimeOffset? StartsAtUtc = null, DateTimeOffset? EndsAtUtc = null);

/// <summary>One game's effect on a player's rating.</summary>
/// <param name="ResultId">The result.</param>
/// <param name="PlayedAtUtc">When the night was.</param>
/// <param name="Placement">Where they finished.</param>
/// <param name="Before">Their rating before.</param>
/// <param name="After">Their rating after.</param>
public record RatingHistoryEntryDto(Guid ResultId, DateTimeOffset PlayedAtUtc, int? Placement, double Before, double After);

/// <summary>A member's standing in one game's league.</summary>
/// <param name="GameId">The game.</param>
/// <param name="GameName">Its name.</param>
/// <param name="Scope">"All time" or the season's name.</param>
/// <param name="Rating">The rating as shown.</param>
/// <param name="Rank">Their position.</param>
/// <param name="Players">How many players are in the table.</param>
/// <param name="GamesPlayed">Confirmed games counted.</param>
public record MyRatingDto(Guid GameId, string GameName, string Scope, int Rating, int Rank, int Players, int GamesPlayed);
