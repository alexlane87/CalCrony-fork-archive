using CalCrony.Contracts.GameNights;

namespace CalCrony.Web.GameNights;

/// <summary>One person in a result being reported on the website: a member or a guest, with their place and
/// their answers to the per-player questions (faction, colour, score...).</summary>
/// <param name="userId">The Discord id, for a member.</param>
/// <param name="guestId">The guest id, for a guest.</param>
/// <param name="name">The "First L" name shown (from the name service, or the guest's typed name).</param>
public sealed class DraftPlayer(long? userId, Guid? guestId, string name)
{
    /// <summary>The Discord id, for a member.</summary>
    public long? UserId { get; } = userId;

    /// <summary>The guest id, for a guest.</summary>
    public Guid? GuestId { get; } = guestId;

    /// <summary>The name shown.</summary>
    public string Name { get; } = name;

    /// <summary>Finishing place as typed (ranked games), or "1"/"2" for the winning side (side games). Not used for
    /// single-winner games, where the draft's <see cref="ReportDraft.Winner"/> says who won.</summary>
    public string Place { get; set; } = "";

    /// <summary>The per-player answers by question key (a number is kept as text, as it is sent).</summary>
    public Dictionary<string, string> Values { get; } = [];

    /// <summary>Whether this is the given member.</summary>
    /// <param name="id">The Discord id.</param>
    /// <returns>True for the same member.</returns>
    public bool IsMember(long id) => UserId == id;
}

/// <summary>The state of the website's "report a result" form, kept apart from the page so the rules can be tested
/// without rendering. It only ever produces the same <see cref="ResultSubmission"/> the bot sends, checked by the same
/// <see cref="ResultValidator"/>. There is no photo anywhere in it: the board photo is posted in the table's Discord
/// thread, never sent to the API.</summary>
/// <param name="game">The game being reported.</param>
public sealed class ReportDraft(GameSpec game)
{
    /// <summary>The game's definition (result type and questions).</summary>
    public GameSpec Game { get; } = game;

    /// <summary>Who played.</summary>
    public List<DraftPlayer> Players { get; } = [];

    /// <summary>The table-wide answers by question key.</summary>
    public Dictionary<string, string> TableValues { get; } = [];

    /// <summary>For a single-winner game, who won.</summary>
    public DraftPlayer? Winner { get; set; }

    /// <summary>Adds a member to the table of players (once).</summary>
    /// <param name="userId">The Discord id.</param>
    /// <param name="name">Their "First L" name.</param>
    /// <returns>The player.</returns>
    public DraftPlayer AddMember(long userId, string name) =>
        Players.FirstOrDefault(p => p.UserId == userId) ?? Add(new DraftPlayer(userId, null, name));

    /// <summary>Adds a guest to the table of players (once).</summary>
    /// <param name="guestId">The guest id.</param>
    /// <param name="name">Their typed "First L" name.</param>
    /// <returns>The player.</returns>
    public DraftPlayer AddGuest(Guid guestId, string name) =>
        Players.FirstOrDefault(p => p.GuestId == guestId) ?? Add(new DraftPlayer(null, guestId, name));

    /// <summary>Takes a player out of the report.</summary>
    /// <param name="player">The player.</param>
    public void Remove(DraftPlayer player)
    {
        Players.Remove(player);
        if (Winner == player)
        {
            Winner = null;
        }
    }

    /// <summary>The questions answered once for the table.</summary>
    public IEnumerable<GameFieldSpec> TableFields =>
        Game.Fields.Where(f => f.Kind != GameFieldKind.Player && f.Scope == GameFieldScope.Table);

    /// <summary>The questions answered for each player.</summary>
    public IEnumerable<GameFieldSpec> PlayerFields =>
        Game.Fields.Where(f => f.Kind != GameFieldKind.Player && f.Scope == GameFieldScope.Player);

    /// <summary>Turns the form into the report the API takes. Empty answers are left out.</summary>
    /// <returns>The report.</returns>
    public ResultSubmission Build() => new(
        [.. Players.Select(p => new SubmittedPlayer(p.UserId, p.GuestId, PlacementOf(p), Answers(p.Values)))],
        Answers(TableValues));

    /// <summary>Everything wrong with the report as it stands, by the shared validator (what the API would refuse).</summary>
    /// <returns>The problems; empty when it can be submitted.</returns>
    public IReadOnlyList<ValidationProblem> Validate() => ResultValidator.Validate(Game, Build());

    private DraftPlayer Add(DraftPlayer player)
    {
        Players.Add(player);
        return player;
    }

    private int? PlacementOf(DraftPlayer player)
    {
        if (Game.ResultType == GameResultType.SingleWinner)
        {
            // Only the winner has a place, as in the Discord flow; the rating treats everyone else as tied with each other.
            return player == Winner ? 1 : null;
        }

        return int.TryParse(player.Place, out var place) ? place : null;
    }

    private static Dictionary<string, string> Answers(Dictionary<string, string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v.Value)).ToDictionary(v => v.Key, v => v.Value.Trim());
}
