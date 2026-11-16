using CalCrony.Bot.GameNights;
using CalCrony.Contracts.GameNights;
using Discord;

namespace CalCrony.Bot.Tests.GameNights;

public class VenuePanelTests
{
    private static readonly Guid EventId = Guid.NewGuid();

    private static IEnumerable<ButtonComponent> Buttons(MessageComponent components) =>
        components.Components.OfType<ActionRowComponent>().SelectMany(row => row.Components).OfType<ButtonComponent>();

    private static VenueAreaDto Area(string name, int capacity, bool active = true) =>
        new(Guid.NewGuid(), name, capacity, active);

    private static EventVenueDto For(IReadOnlyList<VenueAreaDto> areas, IReadOnlyList<Guid> on, EffectiveLimitDto effective, string? address = "1 High Street") =>
        new(EventId, new VenueDto(Guid.NewGuid(), "The Hall", address, 10, true, areas), null, on, effective);

    [Fact]
    public void The_panel_shows_the_venue_the_limit_and_where_it_came_from()
    {
        var upstairs = Area("Upstairs", 5);
        var venue = For([upstairs], [upstairs.Id], new EffectiveLimitDto(15, LimitSource.Venue, "The Hall 10 + Upstairs 5 = 15"));

        var (embed, _) = VenuePanel.Build("Game night", venue);

        Assert.Equal("Venue: Game night", embed.Title);
        Assert.Contains("The Hall", embed.Description);
        Assert.Contains("1 High Street", embed.Description);
        Assert.Contains("**15** places", embed.Description);
        Assert.Contains("The Hall 10 + Upstairs 5 = 15", embed.Description);
    }

    [Fact]
    public void Each_active_area_gets_a_toggle_that_asks_for_the_opposite_state()
    {
        var upstairs = Area("Upstairs", 5);
        var garden = Area("Garden", 8);
        var venue = For([upstairs, garden], [upstairs.Id], new EffectiveLimitDto(15, LimitSource.Venue, null));

        var (_, components) = VenuePanel.Build("Game night", venue);

        var buttons = Buttons(components!).ToList();
        Assert.Equal(2, buttons.Count);
        var on = buttons.Single(b => b.CustomId.Contains(upstairs.Id.ToString()));
        Assert.Equal($"gnarea:{EventId}:{upstairs.Id}:0", on.CustomId); // on now -> pressing switches off
        Assert.Equal(ButtonStyle.Success, on.Style);
        Assert.StartsWith("✓ Upstairs", on.Label);
        var off = buttons.Single(b => b.CustomId.Contains(garden.Id.ToString()));
        Assert.Equal($"gnarea:{EventId}:{garden.Id}:1", off.CustomId);
        Assert.Equal(ButtonStyle.Secondary, off.Style);
        Assert.Equal("Garden +8", off.Label);
    }

    [Fact]
    public void Retired_areas_get_no_button_and_a_venue_with_none_has_no_components()
    {
        var retired = Area("Old cellar", 4, active: false);
        var venue = For([retired], [], new EffectiveLimitDto(10, LimitSource.Venue, null));

        var (_, components) = VenuePanel.Build("Game night", venue);

        Assert.Null(components);
    }

    [Fact]
    public void Custom_ids_stay_within_discords_100_character_limit()
    {
        var area = Area("Upstairs", 5);
        var venue = For([area], [], new EffectiveLimitDto(10, LimitSource.Venue, null));

        var (_, components) = VenuePanel.Build("Game night", venue);

        var button = Buttons(components!).Single();
        Assert.InRange(button.CustomId.Length, 1, 100);
    }

    [Fact]
    public void A_full_venue_fits_five_rows_of_five()
    {
        var areas = Enumerable.Range(1, VenueLimits.MaxAreasPerVenue).Select(i => Area($"Area {i}", i)).ToList();
        var venue = For(areas, [], new EffectiveLimitDto(10, LimitSource.Venue, null));

        var (_, components) = VenuePanel.Build("Game night", venue);

        Assert.Equal(VenueLimits.MaxAreasPerVenue, Buttons(components!).Count());
        Assert.All(components.Components.OfType<ActionRowComponent>(), row => Assert.InRange(row.Components.Count, 1, 5));
    }

    [Fact]
    public void No_limit_reads_as_no_limit()
    {
        var venue = For([], [], new EffectiveLimitDto(null, LimitSource.Venue, "The Hall has no limit"));

        var (embed, _) = VenuePanel.Build("Game night", venue);

        Assert.Contains("No limit", embed.Description);
    }
}

public class BotTierTests
{
    private static readonly TierRoleDto[] Mapping = [new(1, GameNightsTier.Organiser), new(2, GameNightsTier.Moderator)];

    [Fact]
    public void A_server_manager_is_admin_whatever_roles_they_hold() =>
        Assert.Equal(GameNightsTier.Admin, BotTier.TierFor(isManager: true, [], Mapping));

    [Theory]
    [InlineData(new long[0], GameNightsTier.Member)]
    [InlineData(new long[] { 1 }, GameNightsTier.Organiser)]
    [InlineData(new long[] { 1, 2 }, GameNightsTier.Moderator)]
    public void Otherwise_the_highest_mapped_role_wins(long[] held, GameNightsTier expected) =>
        Assert.Equal(expected, BotTier.TierFor(isManager: false, held, Mapping));
}
