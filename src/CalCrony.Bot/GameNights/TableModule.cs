using CalCrony.Bot.Api;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Bot.GameNights;

/// <summary>Autocomplete for a game: the server's active games, picked by id.</summary>
public class GameNameAutocompleteHandler : AutocompleteHandler
{
    /// <inheritdoc />
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        var gameNights = services.GetRequiredService<GameNightsApiClient>();
        var input = autocompleteInteraction.Data.Current.Value?.ToString() ?? "";
        var games = await gameNights.ListGamesAsync((long)context.Guild.Id);
        return AutocompletionResult.FromSuccess(games.Success ? BuildSuggestions(games.Value!, input) : []);
    }

    /// <summary>Filters the games by what was typed.</summary>
    /// <param name="games">The active games.</param>
    /// <param name="input">What the user has typed.</param>
    /// <returns>Up to 25 suggestions whose value is the game id.</returns>
    public static List<AutocompleteResult> BuildSuggestions(IReadOnlyList<GameDto> games, string input) =>
        [.. games
            .Where(g => g.Active && g.Spec.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g.Spec.Name)
            .Take(25)
            .Select(g => new AutocompleteResult(g.Spec.Name, g.Id.ToString()))];
}

/// <summary>Autocomplete for a table: the open tables at the server's upcoming nights, picked by id.</summary>
public class TableAutocompleteHandler : AutocompleteHandler
{
    private const int NightsLooked = 8;

    /// <inheritdoc />
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        var api = services.GetRequiredService<CalCronyApiClient>();
        var gameNights = services.GetRequiredService<GameNightsApiClient>();
        var input = autocompleteInteraction.Data.Current.Value?.ToString() ?? "";
        var guildId = (long)context.Guild.Id;

        var events = await api.ListEventsAsync(guildId, limit: NightsLooked);
        if (!events.Success || events.Value is null)
        {
            return AutocompletionResult.FromSuccess([]);
        }

        // One request per night, in parallel: autocomplete has about three seconds to answer.
        var nights = await Task.WhenAll(events.Value.Select(async e =>
            (Event: e, Tables: (await gameNights.ListTablesAsync(guildId, e.Id)).Value?.Tables ?? [])));
        return AutocompletionResult.FromSuccess(BuildSuggestions(nights, input));
    }

    /// <summary>Pure suggestion shaping: open tables only, filtered by what was typed.</summary>
    /// <param name="nights">Each night with its tables.</param>
    /// <param name="input">What the user has typed.</param>
    /// <returns>Up to 25 suggestions whose value is the table id.</returns>
    public static List<AutocompleteResult> BuildSuggestions(
        IEnumerable<(EventDto Event, IReadOnlyList<TableDto> Tables)> nights, string input) =>
        [.. nights
            .SelectMany(n => n.Tables.Where(t => t.Status == TableStatus.Open).Select(t => (n.Event, Table: t)))
            .Select(x => (x.Table, Label: Label(x.Event, x.Table)))
            .Where(x => x.Label.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(x => new AutocompleteResult(x.Label, x.Table.Id.ToString()))];

    /// <summary>"Root, table 2 · Game night (3/4)", cut to Discord's 100 characters.</summary>
    /// <param name="ev">The night.</param>
    /// <param name="table">The table.</param>
    /// <returns>The label.</returns>
    public static string Label(EventDto ev, TableDto table)
    {
        var label = $"{table.GameName}, table {table.Number} · {ev.Title} ({table.Seats.Count}/{table.Size})";
        return label.Length <= 100 ? label : label[..99] + "…";
    }
}

/// <summary>/table: open a table at a game night (organisers), and sit down at or leave one (everyone).
/// Opening a table queues its Discord thread through the outbox; the commands themselves only talk to the API.</summary>
/// <param name="api">The CalCrony API client.</param>
/// <param name="gameNights">The Game Nights API client.</param>
/// <param name="tiers">The bot-side tier lookup.</param>
/// <param name="reports">Starts a result report.</param>
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
[RequireBotInGuild]
[RequireGameNights]
[Group("table", "Tables at a game night")]
public class TableModule(CalCronyApiClient api, GameNightsApiClient gameNights, BotTier tiers, ReportStarter reports)
    : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Opens a table.</summary>
    /// <param name="eventName">The game night (autocomplete picks it by id).</param>
    /// <param name="game">The game (autocomplete picks it by id).</param>
    /// <param name="size">How many seats; the night's or the game's default when left out.</param>
    /// <param name="host">Who hosts the table; they are seated first.</param>
    [SlashCommand("create", "Open a table at a game night (organisers)")]
    public async Task CreateAsync(
        [Summary("event", "The game night"), Autocomplete(typeof(EventNameAutocompleteHandler))] string eventName,
        [Summary("game", "The game"), Autocomplete(typeof(GameNameAutocompleteHandler))] string game,
        [Summary(description: "Seats at the table"), MinValue(2), MaxValue(100)] int? size = null,
        [Summary(description: "Who is hosting")] IUser? host = null)
    {
        await DeferAsync(ephemeral: true);

        if (Context.User is not SocketGuildUser member || await tiers.ResolveAsync(member) < GameNightsTier.Organiser)
        {
            await FollowupAsync("Only organisers can open tables.", ephemeral: true);
            return;
        }

        var guildId = (long)Context.Guild.Id;
        var (ev, problem) = await EventFinder.FindSingleAsync(api, guildId, eventName);
        if (ev is null)
        {
            await FollowupAsync(problem ?? "I couldn't find that event.", ephemeral: true);
            return;
        }

        var gameId = await ResolveGameAsync(guildId, game);
        if (gameId is null)
        {
            await FollowupAsync("I couldn't find that game. Pick one from the list as you type.", ephemeral: true);
            return;
        }

        var created = await gameNights.CreateTableAsync(
            guildId, ev.Id, new CreateTableRequest(gameId.Value, size, host is null ? null : (long)host.Id));
        if (!created.Success || created.Value is null)
        {
            await FollowupAsync($"❌ {created.Error}", ephemeral: true);
            return;
        }

        var t = created.Value;
        await FollowupAsync(
            $"🎲 **{t.GameName}, table {t.Number}** is open at **{ev.Title}** ({t.Size} seats). Its thread will appear in the channel in a moment.",
            ephemeral: true);
    }

    /// <summary>Sits the caller down at a table.</summary>
    /// <param name="table">The table (autocomplete picks it by id).</param>
    [SlashCommand("join", "Sit down at a table")]
    public async Task JoinAsync(
        [Summary("table", "The table"), Autocomplete(typeof(TableAutocompleteHandler))] string table)
    {
        await DeferAsync(ephemeral: true);
        if (!Guid.TryParse(table, out var id))
        {
            await FollowupAsync("Pick a table from the list as you type.", ephemeral: true);
            return;
        }

        var result = await gameNights.SeatAsync((long)Context.Guild.Id, id, (long)Context.User.Id);
        await FollowupAsync(
            result.Success && result.Value is { } t
                ? $"🪑 You're sitting at **{t.GameName}, table {t.Number}** ({t.Seats.Count}/{t.Size})."
                : $"❌ {result.Error}",
            ephemeral: true);
    }

    /// <summary>Stands the caller up from a table.</summary>
    /// <param name="table">The table (autocomplete picks it by id).</param>
    [SlashCommand("leave", "Leave a table")]
    public async Task LeaveAsync(
        [Summary("table", "The table"), Autocomplete(typeof(TableAutocompleteHandler))] string table)
    {
        await DeferAsync(ephemeral: true);
        if (!Guid.TryParse(table, out var id))
        {
            await FollowupAsync("Pick a table from the list as you type.", ephemeral: true);
            return;
        }

        var result = await gameNights.UnseatAsync((long)Context.Guild.Id, id, (long)Context.User.Id);
        await FollowupAsync(
            result.Success && result.Value is { } t
                ? $"👋 You've left **{t.GameName}, table {t.Number}**."
                : $"❌ {result.Error}",
            ephemeral: true);
    }

    /// <summary>Starts reporting a table's result (the same flow as the Report game button in its thread).</summary>
    /// <param name="table">The table (autocomplete picks it by id).</param>
    [SlashCommand("report", "Report the result of a table")]
    public async Task ReportAsync(
        [Summary("table", "The table"), Autocomplete(typeof(TableAutocompleteHandler))] string table)
    {
        await DeferAsync(ephemeral: true);
        if (Context.User is not SocketGuildUser user || !Guid.TryParse(table, out var id))
        {
            await FollowupAsync("Pick a table from the list as you type.", ephemeral: true);
            return;
        }

        var (error, text, components) = await reports.StartAsync(user, id);
        await (error is not null ? FollowupAsync(error, ephemeral: true) : FollowupAsync(text, components: components, ephemeral: true));
    }

    /// <summary>The game the caller picked: an id from the autocomplete, or (typed by hand) a name.</summary>
    private async Task<Guid?> ResolveGameAsync(long guildId, string game)
    {
        if (Guid.TryParse(game, out var id))
        {
            return id;
        }

        var games = await gameNights.ListGamesAsync(guildId);
        var matches = games.Value?.Where(g => string.Equals(g.Spec.Name, game.Trim(), StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        return matches.Count == 1 ? matches[0].Id : null;
    }
}
