using System.Text;
using CalCrony.Contracts.GameNights;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace CalCrony.Bot.GameNights;

/// <summary>The words and layout of the league commands. Pure, so they are testable.</summary>
public static class LeagueText
{
    private const int RowsShown = 15;
    private const int FieldLimit = 4000;

    /// <summary>A league table as an embed: the top players, with ties sharing a rank, and how many games it is built from.</summary>
    /// <param name="standings">The table.</param>
    /// <returns>The embed.</returns>
    public static Embed Standings(LeagueStandingsDto standings)
    {
        var body = new StringBuilder();
        if (standings.Rows.Count == 0)
        {
            body.Append("Nobody is rated yet. Players join the league by playing: a confirmed result creates their rating.");
        }
        else
        {
            foreach (var row in standings.Rows.Take(RowsShown))
            {
                body.AppendLine($"`{row.Rank,2}.` **{row.DisplayName}** · {row.Rating} · {row.GamesPlayed} game{(row.GamesPlayed == 1 ? "" : "s")}");
            }

            if (standings.Rows.Count > RowsShown)
            {
                body.AppendLine($"…and {standings.Rows.Count - RowsShown} more");
            }
        }

        var text = body.ToString().TrimEnd();
        return new EmbedBuilder()
            .WithTitle($"🏆 {standings.GameName}: {standings.Scope}")
            .WithDescription(text.Length <= FieldLimit ? text : text[..(FieldLimit - 1)] + "…")
            .WithFooter($"{standings.GamesCounted} confirmed game{(standings.GamesCounted == 1 ? "" : "s")} counted · only public tables count")
            .Build();
    }

    /// <summary>A member's ratings across the games they play.</summary>
    /// <param name="ratings">Their ratings.</param>
    /// <returns>The text.</returns>
    public static string Mine(IReadOnlyList<MyRatingDto> ratings) =>
        ratings.Count == 0
            ? "You don't have a league rating yet. You get one when a result you played in is confirmed."
            : string.Join("\n", ratings.Select(r =>
                $"**{r.GameName}** ({r.Scope}): **{r.Rating}**, #{r.Rank} of {r.Players}, {r.GamesPlayed} game{(r.GamesPlayed == 1 ? "" : "s")}"));
}

/// <summary>/league: the standings for a game and your own rating. Read-only: ratings come from confirmed results.</summary>
/// <param name="gameNights">The Game Nights API client.</param>
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
[RequireBotInGuild]
[RequireGameNights]
[Group("league", "Game league tables and ratings")]
public class LeagueModule(GameNightsApiClient gameNights) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Shows a game's league table.</summary>
    /// <param name="game">The game (autocomplete picks it by id).</param>
    /// <param name="allTime">Show the all-time table instead of the running season's.</param>
    [SlashCommand("standings", "Show a game's league table")]
    public async Task StandingsAsync(
        [Summary("game", "The game"), Autocomplete(typeof(GameNameAutocompleteHandler))] string game,
        [Summary("all-time", "Show the all-time table instead of the current season")] bool allTime = false)
    {
        await DeferAsync();
        if (!Guid.TryParse(game, out var gameId))
        {
            await FollowupAsync("Pick a game from the list as you type.", ephemeral: true);
            return;
        }

        var result = await gameNights.GetStandingsAsync((long)Context.Guild.Id, gameId, allTime);
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync(result.NotFound ? "That game isn't in the library." : $"❌ {result.Error}", ephemeral: true);
            return;
        }

        await FollowupAsync(embed: LeagueText.Standings(result.Value), allowedMentions: AllowedMentions.None);
    }

    /// <summary>Shows your own ratings.</summary>
    [SlashCommand("rating", "Show your own league ratings")]
    public async Task RatingAsync()
    {
        await DeferAsync(ephemeral: true);
        var result = await gameNights.GetMyRatingsAsync((long)Context.Guild.Id, (long)Context.User.Id);
        await FollowupAsync(result.Success && result.Value is not null ? LeagueText.Mine(result.Value) : $"❌ {result.Error}", ephemeral: true);
    }
}
