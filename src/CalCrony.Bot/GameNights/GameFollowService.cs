using CalCrony.Contracts.GameNights;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace CalCrony.Bot.GameNights;

/// <summary>Gives or takes a game's follow role. One place for the three callers (the Follow button, /game
/// follow, and the web's follow requests through the outbox), so they all check the same things and say the same
/// thing when Discord refuses. When the bot cannot grant the role it tells the admin through the game's role note
/// (shown on the website) as well as telling the person who asked.</summary>
/// <param name="client">The Discord client.</param>
/// <param name="gameNights">The Game Nights API client.</param>
/// <param name="logger">The logger.</param>
public sealed class GameFollowService(DiscordSocketClient client, GameNightsApiClient gameNights, ILogger<GameFollowService> logger)
{
    /// <summary>The result of a follow, unfollow or toggle.</summary>
    /// <param name="Message">What to tell the person.</param>
    /// <param name="Changed">Whether the member's roles changed.</param>
    /// <param name="NowFollowing">Whether the member follows the game now.</param>
    public readonly record struct Outcome(string Message, bool Changed, bool NowFollowing);

    /// <summary>Flips the member's follow state: takes the role if they have it, gives it if not.</summary>
    /// <param name="user">The member.</param>
    /// <param name="game">The game.</param>
    /// <returns>What happened.</returns>
    public Task<Outcome> ToggleAsync(SocketGuildUser user, GameDto game) =>
        SetAsync(user.Guild, user, game, follow: game.Spec.RoleId is { } role ? !user.Roles.Any(r => (long)r.Id == role) : true);

    /// <summary>Gives or takes the role for a member the bot looks up (the web's request).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="userId">The member.</param>
    /// <param name="game">The game.</param>
    /// <param name="follow">True to give the role, false to take it.</param>
    /// <returns>What happened.</returns>
    public async Task<Outcome> SetForAsync(long guildId, long userId, GameDto game, bool follow)
    {
        var guild = client.GetGuild((ulong)guildId);
        var user = guild?.GetUser((ulong)userId);
        return guild is null || user is null
            ? new Outcome("That member isn't in the server.", false, false)
            : await SetAsync(guild, user, game, follow);
    }

    private async Task<Outcome> SetAsync(SocketGuild guild, SocketGuildUser user, GameDto game, bool follow)
    {
        if (game.Spec.RoleId is not { } roleId)
        {
            return new Outcome($"**{game.Spec.Name}** has no follow role yet.", false, false);
        }

        var role = guild.GetRole((ulong)roleId);
        if (role is null)
        {
            const string gone = "The game's role was deleted in Discord. Attach or create a new one.";
            await ReportAsync(guild, game, gone);
            return new Outcome($"{gone} Ask an organiser.", false, false);
        }

        var me = guild.CurrentUser;
        var warning = GameRoleChecks.CannotGrant(
            role.Name, me.GuildPermissions.ManageRoles, me.Hierarchy, role.Position, role.IsManaged, role.IsEveryone);
        if (warning is not null)
        {
            await ReportAsync(guild, game, warning);
            return new Outcome($"⚠️ {warning}", false, false);
        }

        var has = user.Roles.Any(r => r.Id == role.Id);
        try
        {
            if (follow && !has)
            {
                await user.AddRoleAsync(role);
            }
            else if (!follow && has)
            {
                await user.RemoveRoleAsync(role);
            }
            else
            {
                return new Outcome(follow ? $"You already follow **{game.Spec.Name}**." : $"You weren't following **{game.Spec.Name}**.", false, follow);
            }
        }
        catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            logger.LogWarning(ex, "Discord refused to change role {RoleId} for {UserId} in guild {GuildId}.", roleId, user.Id, guild.Id);
            const string refused = "Discord refused: the bot's role must sit above the game's role and have Manage Roles.";
            await ReportAsync(guild, game, refused);
            return new Outcome($"⚠️ {refused}", false, has);
        }

        // A role this bot changes clears any earlier failure note: it plainly works now.
        if (game.RoleState == GameRoleState.Failed)
        {
            await gameNights.SetGameRoleStatusAsync((long)guild.Id, game.Id, new SetGameRoleStatusRequest(null, GameRoleState.None));
        }

        return new Outcome(
            follow ? $"🔔 You're following **{game.Spec.Name}**. You'll have the **{role.Name}** role." : $"🔕 You've stopped following **{game.Spec.Name}**.",
            true, follow);
    }

    private Task ReportAsync(SocketGuild guild, GameDto game, string note) =>
        gameNights.SetGameRoleStatusAsync((long)guild.Id, game.Id, new SetGameRoleStatusRequest(null, GameRoleState.Failed, note));
}
