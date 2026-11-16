namespace CalCrony.Contracts.GameNights;

/// <summary>Who may see an event or table. Public is the only value with any UI in phase 1; the
/// others are reserved so the column and the filter exist from the start. Stored as the number.</summary>
public enum GameNightsVisibility
{
    /// <summary>Everyone in the server.</summary>
    Public = 0,

    /// <summary>Invitees, and organisers and above.</summary>
    Restricted = 1,

    /// <summary>Invitees, and moderators and above.</summary>
    InviteOnly = 2,
}

/// <summary>Pure visibility rules, shared by the API's query filter and any client that wants to
/// mirror them. A row the caller may not see is reported as not found, never forbidden.</summary>
public static class VisibilityRules
{
    /// <summary>Whether a caller may see a row.</summary>
    /// <param name="visibility">The row's visibility.</param>
    /// <param name="tier">The caller's tier.</param>
    /// <param name="isInvitee">Whether the caller is on the row's invite list.</param>
    /// <returns>True when the row is visible to the caller.</returns>
    public static bool CanSee(GameNightsVisibility visibility, GameNightsTier tier, bool isInvitee) => visibility switch
    {
        GameNightsVisibility.Public => true,
        GameNightsVisibility.Restricted => isInvitee || tier >= GameNightsTier.Organiser,
        GameNightsVisibility.InviteOnly => isInvitee || tier >= GameNightsTier.Moderator,
        _ => false,
    };

    /// <summary>Whether a row of this visibility may carry a league entry. Only public rows may:
    /// league results and ratings are retained indefinitely and shown to everyone, so a private
    /// table must never feed one. Step 9 backs this with a database check constraint.</summary>
    /// <param name="visibility">The row's visibility.</param>
    /// <returns>True only for <see cref="GameNightsVisibility.Public"/>.</returns>
    public static bool CanCarryLeague(GameNightsVisibility visibility) => visibility == GameNightsVisibility.Public;
}
