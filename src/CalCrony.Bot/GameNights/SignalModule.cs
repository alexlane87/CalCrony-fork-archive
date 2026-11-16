using CalCrony.Bot.Api;
using CalCrony.Contracts.GameNights;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.GameNights;

/// <summary>The buttons and selects behind an event post's Pick games button. Opening the panel and every
/// change are private to the member (an ephemeral message); each select saves straight away through the API,
/// which queues a single delayed re-render of the public post, so a crowd answering produces a few edits, not
/// one per click.</summary>
/// <param name="api">The CalCrony API client.</param>
/// <param name="gameNights">The Game Nights API client.</param>
[RequireBotInGuild]
[RequireGameNights]
public class SignalModule(CalCronyApiClient api, GameNightsApiClient gameNights)
    : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Opens the panel: a private page of the server's games to answer about.</summary>
    /// <param name="eventIdRaw">The event id.</param>
    [ComponentInteraction($"{GameNightsEmbed.PickPrefix}:*")]
    public async Task OpenAsync(string eventIdRaw)
    {
        await DeferAsync(ephemeral: true);
        if (!Guid.TryParse(eventIdRaw, out var eventId))
        {
            await FollowupAsync("This event no longer exists.", ephemeral: true);
            return;
        }

        var panel = await BuildAsync(eventId, page: 0);
        if (panel.Error is not null)
        {
            await FollowupAsync(panel.Error, ephemeral: true);
            return;
        }

        await FollowupAsync(embed: panel.Embed, components: panel.Components, ephemeral: true);
    }

    /// <summary>A select changed: save the answer, then redraw the same page.</summary>
    /// <param name="eventIdRaw">The event id.</param>
    /// <param name="gameIdRaw">The game id.</param>
    /// <param name="values">The chosen option.</param>
    [ComponentInteraction($"{SignalPanel.SelectPrefix}:*:*")]
    public async Task ChooseAsync(string eventIdRaw, string gameIdRaw, string[] values)
    {
        await DeferAsync();
        if (!Guid.TryParse(eventIdRaw, out var eventId) || !Guid.TryParse(gameIdRaw, out var gameId))
        {
            await FollowupAsync("That panel is out of date. Press Pick games again.", ephemeral: true);
            return;
        }

        var choice = values.FirstOrDefault();
        SignalKind? kind = choice == SignalPanel.ClearValue ? null : Enum.TryParse<SignalKind>(choice, out var parsed) ? parsed : null;
        if (choice != SignalPanel.ClearValue && kind is null)
        {
            await FollowupAsync("I didn't understand that choice.", ephemeral: true);
            return;
        }

        var saved = await gameNights.SetSignalAsync((long)Context.Guild.Id, eventId, gameId, kind, (long)Context.User.Id);
        if (!saved.Success)
        {
            await FollowupAsync($"❌ {saved.Error}", ephemeral: true);
            return;
        }

        var panel = await BuildAsync(eventId, page: null, pageOfGame: gameId);
        if (panel.Error is not null)
        {
            await FollowupAsync(panel.Error, ephemeral: true);
            return;
        }

        await ModifyOriginalResponseAsync(m => { m.Embed = panel.Embed; m.Components = panel.Components; });
    }

    /// <summary>Previous or next page.</summary>
    /// <param name="eventIdRaw">The event id.</param>
    /// <param name="pageRaw">The page to show.</param>
    [ComponentInteraction($"{SignalPanel.PagePrefix}:*:*")]
    public async Task PageAsync(string eventIdRaw, string pageRaw)
    {
        await DeferAsync();
        if (!Guid.TryParse(eventIdRaw, out var eventId) || !int.TryParse(pageRaw, out var page))
        {
            await FollowupAsync("That panel is out of date. Press Pick games again.", ephemeral: true);
            return;
        }

        var panel = await BuildAsync(eventId, page);
        if (panel.Error is not null)
        {
            await FollowupAsync(panel.Error, ephemeral: true);
            return;
        }

        await ModifyOriginalResponseAsync(m => { m.Embed = panel.Embed; m.Components = panel.Components; });
    }

    private async Task<(Embed? Embed, MessageComponent? Components, string? Error)> BuildAsync(Guid eventId, int? page, Guid? pageOfGame = null)
    {
        var guildId = (long)Context.Guild.Id;
        var ev = await api.GetEventAsync(eventId);
        if (!ev.Success || ev.Value is null || ev.Value.GuildId != guildId)
        {
            return (null, null, "This event no longer exists.");
        }

        var games = await gameNights.ListGamesAsync(guildId);
        if (!games.Success || games.Value is null)
        {
            return (null, null, $"❌ {games.Error}");
        }

        if (games.Value.Count == 0)
        {
            return (null, null, "There are no games in the library yet. An organiser can add some with /game add.");
        }

        var mine = await gameNights.GetMySignalsAsync(guildId, eventId, (long)Context.User.Id);
        if (!mine.Success || mine.Value is null)
        {
            return (null, null, $"❌ {mine.Error}");
        }

        var answers = mine.Value.Signals.ToDictionary(s => s.GameId, s => s.Kind);
        var shownPage = pageOfGame is { } game ? SignalPanel.PageOf(games.Value, game) : page ?? 0;
        var (embed, components) = SignalPanel.Build(eventId, ev.Value.Title, games.Value, answers, shownPage);
        return (embed, components, null);
    }
}
