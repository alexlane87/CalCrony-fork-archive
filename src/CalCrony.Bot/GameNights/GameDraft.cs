using System.Collections.Concurrent;
using CalCrony.Contracts.GameNights;

namespace CalCrony.Bot.GameNights;

/// <summary>A game being set up through the /game add steps. Each step is a separate Discord interaction, so
/// the choices so far live here, keyed by a short random id carried in the components' custom ids. Only the
/// person who started it may continue it.</summary>
public sealed class GameDraft
{
    /// <summary>The draft's id (in every component custom id of the flow).</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The Discord user who started it.</summary>
    public ulong OwnerId { get; init; }

    /// <summary>The Discord guild (server) id.</summary>
    public ulong GuildId { get; init; }

    /// <summary>The game's name.</summary>
    public string Name { get; init; } = "";

    /// <summary>The fewest players.</summary>
    public int MinSize { get; init; }

    /// <summary>The most players.</summary>
    public int MaxSize { get; init; }

    /// <summary>The starting table size.</summary>
    public int DefaultSize { get; init; }

    /// <summary>How a result is recorded, once chosen.</summary>
    public GameResultType? ResultType { get; set; }

    /// <summary>The starting questions, once chosen.</summary>
    public GameWizard.FieldTemplate? Template { get; set; }

    /// <summary>When the draft is thrown away if nobody finishes it.</summary>
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow + GameDraftStore.Lifetime;

    /// <summary>Builds the definition this draft describes.</summary>
    /// <param name="roleId">The follow role, if one has been chosen or made.</param>
    /// <param name="takenKeys">Keys already in use on the server.</param>
    /// <returns>The game definition.</returns>
    public GameSpec ToSpec(long? roleId, IEnumerable<string> takenKeys) => GameWizard.Build(
        Name, ResultType ?? GameResultType.RankedPlacement, MinSize, MaxSize, DefaultSize,
        Template ?? GameWizard.FieldTemplate.PlayersAndScores, roleId, takenKeys);
}

/// <summary>Holds in-progress drafts for a quarter of an hour. In memory: the bot is one process, and a draft
/// lost to a restart just means starting /game add again.</summary>
public sealed class GameDraftStore
{
    /// <summary>How long an unfinished draft lives.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<Guid, GameDraft> drafts = new();

    /// <summary>Remembers a draft (and forgets any that have expired).</summary>
    /// <param name="draft">The draft.</param>
    public void Add(GameDraft draft)
    {
        foreach (var (id, old) in drafts)
        {
            if (old.ExpiresAt < DateTimeOffset.UtcNow)
            {
                drafts.TryRemove(id, out _);
            }
        }

        drafts[draft.Id] = draft;
    }

    /// <summary>Finds a live draft that belongs to the caller, and gives it a fresh lifetime.</summary>
    /// <param name="id">The draft id from the custom id.</param>
    /// <param name="userId">The user pressing the component.</param>
    /// <returns>The draft, or null when it is unknown, expired or someone else's.</returns>
    public GameDraft? Get(Guid id, ulong userId)
    {
        if (!drafts.TryGetValue(id, out var draft) || draft.OwnerId != userId || draft.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return null;
        }

        draft.ExpiresAt = DateTimeOffset.UtcNow + Lifetime;
        return draft;
    }

    /// <summary>Forgets a draft (it was finished or abandoned).</summary>
    /// <param name="id">The draft id.</param>
    public void Remove(Guid id) => drafts.TryRemove(id, out _);
}
