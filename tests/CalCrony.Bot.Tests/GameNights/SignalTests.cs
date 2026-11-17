using CalCrony.Bot;
using CalCrony.Bot.GameNights;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Discord;

namespace CalCrony.Bot.Tests.GameNights;

public class GameNightsEmbedTests
{
    private static GameDemandDto Demand(string name, int teach = 0, int play = 0, int fresh = 0, int pass = 0) =>
        new(Guid.NewGuid(), name, teach, play, fresh, pass);

    private static NightSignalsSummary Summary(params GameDemandDto[] games) =>
        new(games, Teachers: 2, Experienced: 4, Fresh: 3, Respondents: 7);

    private static EventDto Event(NightSignalsSummary? summary, int options = 3, int seatedEach = 2, string? description = null)
    {
        var opts = Enumerable.Range(0, options).Select(i => new RsvpOptionDto(Guid.NewGuid(), "✅", $"Option {i}", i, null, IsAttending: i == 0)).ToList();
        var rsvps = opts.SelectMany(o => Enumerable.Range(0, seatedEach).Select(i => new RsvpDto(1000 + i, o.Id))).ToList();
        return new EventDto(
            Guid.NewGuid(), 1, 2, "Game night", description, DateTimeOffset.UtcNow.AddDays(1), "UTC", null, 3, null, null, null,
            EventStatus.Scheduled, opts, rsvps, GameNights: summary);
    }

    [Fact]
    public void With_no_answers_the_post_invites_people_to_pick()
    {
        var text = GameNightsEmbed.SummaryText(new NightSignalsSummary([Demand("Root")], 0, 0, 0, 0));

        Assert.Contains("Nobody has picked games yet", text);
        Assert.Contains("Pick games", text);
    }

    [Fact]
    public void The_summary_counts_people_and_lists_the_busiest_games_flagging_any_without_a_teacher()
    {
        var text = GameNightsEmbed.SummaryText(Summary(Demand("Root", teach: 1, play: 2, fresh: 3), Demand("Arcs", play: 1, fresh: 1), Demand("Go")));

        Assert.Contains("**7** answered", text);
        Assert.Contains("2 teaching", text);
        Assert.Contains("4 know their games", text);
        Assert.Contains("3 new to something", text);
        Assert.Contains("**Root**: 6 keen · 1 can teach", text);
        Assert.Contains("**Arcs**: 2 keen · ⚠️ no teacher yet", text);
        Assert.DoesNotContain("Go", text); // nobody wants it, so it is not listed
    }

    [Fact]
    public void A_long_list_is_cut_to_the_top_games_with_a_count_of_the_rest_and_never_exceeds_the_field_budget()
    {
        var games = Enumerable.Range(0, 40).Select(i => Demand(new string((char)('A' + i % 26), 60), play: 40 - i)).ToArray();

        var text = GameNightsEmbed.SummaryText(Summary(games));

        Assert.True(text.Length <= 600, $"{text.Length} chars");
        Assert.Contains("more", text);
        Assert.InRange(text.Split('\n').Length, 2, 8);
    }

    [Fact]
    public void An_event_with_no_summary_renders_exactly_as_before()
    {
        var plain = Event(summary: null);

        var embed = EventEmbedBuilder.Build(plain);
        var components = EventEmbedBuilder.BuildComponents(plain);

        Assert.DoesNotContain(embed.Fields, f => f.Name == GameNightsEmbed.FieldName);
        Assert.DoesNotContain(AllButtons(components), b => b.CustomId.StartsWith(GameNightsEmbed.PickPrefix));
    }

    [Fact]
    public void An_event_with_a_summary_gets_the_field_and_a_pick_games_button_after_the_rsvp_buttons()
    {
        var ev = Event(Summary(Demand("Root", teach: 1)));

        var embed = EventEmbedBuilder.Build(ev);
        var buttons = AllButtons(EventEmbedBuilder.BuildComponents(ev)).ToList();

        Assert.Contains(embed.Fields, f => f.Name == GameNightsEmbed.FieldName && f.Value.Contains("**Root**"));
        Assert.Equal($"{GameNightsEmbed.PickPrefix}:{ev.Id}", buttons[^1].CustomId);
        Assert.Equal(4, buttons.Count); // three RSVP options and the new one
    }

    [Fact]
    public void A_full_row_of_rsvp_buttons_pushes_the_button_onto_a_new_row_and_five_full_rows_leave_it_out()
    {
        var five = EventEmbedBuilder.BuildComponents(Event(Summary(Demand("Root")), options: 5));
        Assert.Equal(2, five.Components.OfType<ActionRowComponent>().Count());
        Assert.Contains(AllButtons(five), b => b.CustomId.StartsWith(GameNightsEmbed.PickPrefix));

        var packed = EventEmbedBuilder.BuildComponents(Event(Summary(Demand("Root")), options: 25));
        Assert.Equal(5, packed.Components.OfType<ActionRowComponent>().Count());
        Assert.DoesNotContain(AllButtons(packed), b => b.CustomId.StartsWith(GameNightsEmbed.PickPrefix)); // no room: Discord's five rows are full
    }

    [Fact]
    public void Even_a_maximal_event_with_the_summary_stays_inside_discords_embed_limits()
    {
        var games = Enumerable.Range(0, 30).Select(i => Demand($"Game number {i} with a longish name", play: 30 - i)).ToArray();
        var ev = Event(Summary(games), options: 6, seatedEach: 80, description: new string('x', 5000));

        var embed = EventEmbedBuilder.Build(ev);

        var total = (embed.Title?.Length ?? 0) + (embed.Description?.Length ?? 0) + (embed.Footer?.Text.Length ?? 0)
                    + embed.Fields.Sum(f => f.Name.Length + f.Value.Length);
        Assert.True(total <= 6000, $"embed is {total} characters");
        Assert.All(embed.Fields, f => Assert.True(f.Value.Length <= 1024));
        Assert.Contains(embed.Fields, f => f.Name == GameNightsEmbed.FieldName);
    }

    private static IEnumerable<ButtonComponent> AllButtons(MessageComponent components) =>
        components.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>();
}

public class SignalPanelTests
{
    private static readonly Guid EventId = Guid.NewGuid();

    private static List<GameDto> Games(int count) => [.. Enumerable.Range(1, count).Select(i => new GameDto(
        Guid.NewGuid(), true,
        new GameSpec($"g{i}", $"Game {i}", GameResultType.RankedPlacement, 2, 4, 4, true, 32, 1000, [])))];

    private static IEnumerable<SelectMenuComponent> Selects(MessageComponent c) =>
        c.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<SelectMenuComponent>();

    [Theory]
    [InlineData(0, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(25, 7)]
    public void The_games_are_paged_four_at_a_time(int games, int pages) =>
        Assert.Equal(pages, SignalPanel.PageCount(games));

    [Fact]
    public void A_page_has_one_select_per_game_with_all_the_answers_the_api_understands_plus_clear()
    {
        var games = Games(6);

        var (_, components) = SignalPanel.Build(EventId, "Friday", games, new Dictionary<Guid, SignalKind>(), 0);

        var selects = Selects(components).ToList();
        Assert.Equal(4, selects.Count);
        Assert.Equal($"gnsig:{EventId}:{games[0].Id}", selects[0].CustomId);
        var values = selects[0].Options.Select(o => o.Value).ToList();
        Assert.Equal([.. Enum.GetNames<SignalKind>(), SignalPanel.ClearValue], values);
        Assert.All(selects.SelectMany(s => s.Options), o => Assert.InRange(o.Label.Length, 1, 100));
        Assert.All(selects, s => Assert.InRange(s.CustomId.Length, 1, 100));
    }

    [Fact]
    public void The_members_current_answer_is_marked_as_selected_and_unanswered_games_have_none()
    {
        var games = Games(2);
        var mine = new Dictionary<Guid, SignalKind> { [games[0].Id] = SignalKind.NewToIt };

        var selects = Selects(SignalPanel.Build(EventId, "Friday", games, mine, 0).Components).ToList();

        Assert.Equal([nameof(SignalKind.NewToIt)], selects[0].Options.Where(o => o.IsDefault == true).Select(o => o.Value));
        Assert.DoesNotContain(selects[1].Options, o => o.IsDefault == true);
        Assert.Contains("Game 1", selects[0].Options.First().Label); // the game's name travels with every option
    }

    [Fact]
    public void Paging_buttons_appear_only_with_more_than_one_page_and_disable_at_the_ends()
    {
        var games = Games(9);

        var first = SignalPanel.Build(EventId, "Friday", games, new Dictionary<Guid, SignalKind>(), 0).Components;
        var last = SignalPanel.Build(EventId, "Friday", games, new Dictionary<Guid, SignalKind>(), 2).Components;
        var single = SignalPanel.Build(EventId, "Friday", Games(3), new Dictionary<Guid, SignalKind>(), 0).Components;

        var firstButtons = first.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>().ToList();
        Assert.True(firstButtons[0].IsDisabled);
        Assert.False(firstButtons[1].IsDisabled);
        Assert.Equal($"gnpage:{EventId}:1", firstButtons[1].CustomId);
        Assert.Single(Selects(last)); // the ninth game sits alone on the last page
        var lastButtons = last.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>().ToList();
        Assert.True(lastButtons[1].IsDisabled);
        Assert.DoesNotContain(single.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components), c => c is ButtonComponent);
        Assert.Equal(5, first.Components.OfType<ActionRowComponent>().Count()); // four selects and the buttons: Discord's five rows
    }

    [Fact]
    public void A_page_number_out_of_range_is_pulled_back_in_and_a_changed_game_finds_its_page()
    {
        var games = Games(6);

        var (embed, components) = SignalPanel.Build(EventId, "Friday", games, new Dictionary<Guid, SignalKind>(), 99);

        Assert.Equal(2, Selects(components).Count()); // the last page: games 5 and 6
        Assert.Contains("Page 2 of 2", embed.Description);
        Assert.Equal(1, SignalPanel.PageOf(games, games[5].Id));
        Assert.Equal(0, SignalPanel.PageOf(games, games[3].Id));
        Assert.Equal(0, SignalPanel.PageOf(games, Guid.NewGuid()));
    }

    [Fact]
    public void Every_answer_has_words_for_it()
    {
        Assert.All(Enum.GetValues<SignalKind>(), k => Assert.False(string.IsNullOrWhiteSpace(SignalPanel.Describe(k))));
    }
}
