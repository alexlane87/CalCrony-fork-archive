using CalCrony.Bot.GameNights;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;

namespace CalCrony.Bot.Tests.GameNights;

public class TableTextTests
{
    private static TableDto Table(long? host, params long[] seated) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Root", 2, 4, host, TableStatus.Open, null, GameNightsVisibility.Public,
        [.. seated.Select((id, i) => new TableSeatDto(id, i))]);

    [Fact]
    public void The_thread_name_names_the_game_table_and_night_and_fits_discords_limit()
    {
        Assert.Equal("Root, table 2: Game night", TableText.ThreadName(Table(null), "Game night"));
        Assert.Equal(100, TableText.ThreadName(Table(null), new string('x', 300)).Length);
    }

    [Fact]
    public void The_host_is_prompted_first_and_is_the_only_person_that_message_pings()
    {
        var table = Table(host: 11, 11, 12, 13);

        var (text, mentions) = TableText.HostPrompt(table);

        Assert.Contains("<@11>", text);
        Assert.Contains("you're hosting", text);
        Assert.Equal([11UL], mentions);
    }

    [Fact]
    public void Without_a_host_the_first_message_pings_nobody_and_says_so()
    {
        var (text, mentions) = TableText.HostPrompt(Table(null, 12));

        Assert.Contains("No host", text);
        Assert.Empty(mentions);
    }

    [Fact]
    public void The_welcome_names_the_other_players_in_seat_order_and_skips_the_host()
    {
        var table = Table(host: 11, 11, 13, 12);

        var welcome = TableText.PlayersWelcome(table);

        Assert.NotNull(welcome);
        Assert.Equal([13UL, 12UL], welcome!.Value.Mentions);
        Assert.Contains("3 of 4 seats", welcome.Value.Text);
        Assert.DoesNotContain("<@11>", welcome.Value.Text);
    }

    [Fact]
    public void There_is_no_welcome_when_only_the_host_is_seated_or_nobody_is()
    {
        Assert.Null(TableText.PlayersWelcome(Table(host: 11, 11)));
        Assert.Null(TableText.PlayersWelcome(Table(null)));
    }

    [Fact]
    public void Seat_notes_say_who_and_how_full_the_table_is()
    {
        Assert.Equal("🪑 <@5> sat down (3/4).", TableText.SeatChange(new TableSeatChangePayload(Guid.NewGuid(), 1, 5, true), 3, 4));
        Assert.Equal("👋 <@5> left the table (2/4).", TableText.SeatChange(new TableSeatChangePayload(Guid.NewGuid(), 1, 5, false), 2, 4));
    }

    [Fact]
    public void The_starter_shows_seats_and_the_host()
    {
        var embed = TableText.Starter(Table(host: 11, 11, 12), "Game night");

        Assert.Contains("2 of 4 seats taken", embed.Description);
        Assert.Contains("Host: <@11>", embed.Description);
        Assert.Contains("Root, table 2", embed.Title);
    }
}

public class TableAutocompleteTests
{
    private static EventDto Event(string title) => new(
        Guid.NewGuid(), 1, 2, title, null, DateTimeOffset.UtcNow.AddDays(1), "UTC", null, 3, null, null, null,
        EventStatus.Scheduled, [], []);

    private static TableDto Table(string game, int number, TableStatus status = TableStatus.Open, int seated = 1) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), game, number, 4, null, status, null, GameNightsVisibility.Public,
        [.. Enumerable.Range(0, seated).Select(i => new TableSeatDto(100 + i, i))]);

    [Fact]
    public void Only_open_tables_are_offered_with_a_label_that_says_which_and_how_full()
    {
        var night = Event("Friday");
        var open = Table("Root", 1, seated: 3);
        var done = Table("Arcs", 2, TableStatus.Played);

        var suggestions = TableAutocompleteHandler.BuildSuggestions([(night, [open, done])], "");

        var only = Assert.Single(suggestions);
        Assert.Equal("Root, table 1 · Friday (3/4)", only.Name);
        Assert.Equal(open.Id.ToString(), only.Value);
    }

    [Fact]
    public void Typing_filters_by_game_night_or_number_and_labels_fit_discords_limit()
    {
        var friday = Event("Friday");
        var saturday = Event(new string('s', 150));
        var nights = new (EventDto, IReadOnlyList<TableDto>)[]
        {
            (friday, [Table("Root", 1)]), (saturday, [Table("Arcs", 2)]),
        };

        Assert.Equal("Root, table 1 · Friday (1/4)", Assert.Single(TableAutocompleteHandler.BuildSuggestions(nights, "root")).Name);
        Assert.Single(TableAutocompleteHandler.BuildSuggestions(nights, "ssss"));
        Assert.All(TableAutocompleteHandler.BuildSuggestions(nights, ""), s => Assert.InRange(s.Name.Length, 1, 100));
    }

    [Fact]
    public void Games_are_offered_by_name_and_retired_ones_are_not()
    {
        GameDto Game(string name, bool active) => new(Guid.NewGuid(), active,
            new GameSpec(name.ToLowerInvariant(), name, GameResultType.RankedPlacement, 2, 4, 4, true, 32, 1000, []));

        var suggestions = GameNameAutocompleteHandler.BuildSuggestions(
            [Game("Root", true), Game("Arcs", true), Game("Rootbeer", false)], "ro");

        Assert.Equal(["Root"], suggestions.Select(s => s.Name));
    }

    [Fact]
    public void The_table_handler_owns_exactly_its_two_delivery_types()
    {
        var handler = new TableDeliveryHandler(null!, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<TableDeliveryHandler>.Instance);

        Assert.True(handler.Handles(DeliveryType.CreateTableThread));
        Assert.True(handler.Handles(DeliveryType.TableSeatChange));
        Assert.False(handler.Handles(DeliveryType.Reminder));
        Assert.False(handler.Handles(DeliveryType.SyncEventMessage));
    }
}
