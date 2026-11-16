namespace CalCrony.Bot.GameNights;

/// <summary>Whether the bot can create a role or give one to members, and if not, what to tell the person
/// setting a game up. Pure (plain facts in, text out), so every refusal is testable. The warnings name the
/// fix, because "missing permissions" alone sends an admin hunting.</summary>
public static class GameRoleChecks
{
    /// <summary>What stops the bot creating a role, if anything.</summary>
    /// <param name="botCanManageRoles">Whether the bot holds Manage Roles in the server.</param>
    /// <returns>A warning, or null when it can.</returns>
    public static string? CannotCreate(bool botCanManageRoles) =>
        botCanManageRoles
            ? null
            : "I can't create roles here: my role needs the **Manage Roles** permission. " +
              "Turn it on under Server Settings → Roles, then try again.";

    /// <summary>What stops the bot giving an existing role to members, if anything. A role can only be given
    /// by a bot whose highest role sits above it in the list, and never an integration's managed role or @everyone.</summary>
    /// <param name="roleName">The role's name, for the message.</param>
    /// <param name="botCanManageRoles">Whether the bot holds Manage Roles.</param>
    /// <param name="botHighestPosition">The position of the bot's highest role (higher is above).</param>
    /// <param name="rolePosition">The position of the role to give.</param>
    /// <param name="roleIsManaged">Whether an integration owns the role.</param>
    /// <param name="roleIsEveryone">Whether it is the @everyone role.</param>
    /// <returns>A warning, or null when the bot can give it.</returns>
    public static string? CannotGrant(
        string roleName, bool botCanManageRoles, int botHighestPosition, int rolePosition, bool roleIsManaged, bool roleIsEveryone)
    {
        if (roleIsEveryone)
        {
            return "@everyone can't be a game role: everyone already has it.";
        }

        if (roleIsManaged)
        {
            return $"**{roleName}** is managed by an integration, so I can't give it to people. Pick another role or create a new one.";
        }

        if (!botCanManageRoles)
        {
            return "I can't give out roles here: my role needs the **Manage Roles** permission. " +
                   "Turn it on under Server Settings → Roles, then try again.";
        }

        return botHighestPosition <= rolePosition
            ? $"I can't give out **{roleName}** because it sits at or above my role. " +
              "In Server Settings → Roles, drag my role above it, then try again."
            : null;
    }
}
