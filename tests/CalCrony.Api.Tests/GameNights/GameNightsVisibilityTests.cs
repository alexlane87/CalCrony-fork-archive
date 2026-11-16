using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.GameNights;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests.GameNights;

/// <summary>Pure tier and visibility rules (no database).</summary>
public class GameNightsRulesTests
{
    private static readonly TierRoleDto[] Mapping =
    [
        new(1, GameNightsTier.Organiser), new(2, GameNightsTier.Moderator), new(3, GameNightsTier.Admin),
    ];

    [Theory]
    [InlineData(new long[0], GameNightsTier.Member)]
    [InlineData(new long[] { 99 }, GameNightsTier.Member)]
    [InlineData(new long[] { 1 }, GameNightsTier.Organiser)]
    [InlineData(new long[] { 2 }, GameNightsTier.Moderator)]
    [InlineData(new long[] { 3 }, GameNightsTier.Admin)]
    [InlineData(new long[] { 1, 2 }, GameNightsTier.Moderator)]
    [InlineData(new long[] { 3, 1, 99 }, GameNightsTier.Admin)]
    public void Tier_is_the_highest_mapped_role_held(long[] held, GameNightsTier expected) =>
        Assert.Equal(expected, TierLogic.Resolve(held, Mapping));

    [Fact]
    public void An_empty_mapping_makes_everyone_a_member() =>
        Assert.Equal(GameNightsTier.Member, TierLogic.Resolve([1, 2, 3], []));

    [Theory]
    [InlineData(GameNightsVisibility.Public, GameNightsTier.Member, false, true)]
    [InlineData(GameNightsVisibility.Restricted, GameNightsTier.Member, false, false)]
    [InlineData(GameNightsVisibility.Restricted, GameNightsTier.Member, true, true)]
    [InlineData(GameNightsVisibility.Restricted, GameNightsTier.Organiser, false, true)]
    [InlineData(GameNightsVisibility.InviteOnly, GameNightsTier.Member, false, false)]
    [InlineData(GameNightsVisibility.InviteOnly, GameNightsTier.Organiser, false, false)]
    [InlineData(GameNightsVisibility.InviteOnly, GameNightsTier.Moderator, false, true)]
    [InlineData(GameNightsVisibility.InviteOnly, GameNightsTier.Member, true, true)]
    public void Visibility_rule(GameNightsVisibility visibility, GameNightsTier tier, bool invitee, bool expected) =>
        Assert.Equal(expected, VisibilityRules.CanSee(visibility, tier, invitee));

    [Theory]
    [InlineData(GameNightsVisibility.Public, true)]
    [InlineData(GameNightsVisibility.Restricted, false)]
    [InlineData(GameNightsVisibility.InviteOnly, false)]
    public void Only_public_rows_may_carry_a_league(GameNightsVisibility visibility, bool expected) =>
        Assert.Equal(expected, VisibilityRules.CanCarryLeague(visibility));
}

/// <summary>The EF visibility filter agrees with the pure rule, and a hidden event is reported as
/// "not visible" (callers turn that into a 404). The tables are created by the migration whether or
/// not the feature flag is on, so this runs on the default fixture.</summary>
public class GameNightsVisibilityTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const long GuildId = 52000;
    private const long Invited = 900001;
    private const long NotInvited = 900002;

    [Fact]
    public async Task The_filter_matches_the_rule_for_every_visibility_tier_and_invite_combination()
    {
        var publicEvent = await CreateEventAsync("Public");
        var restricted = await CreateEventAsync("Restricted");
        var inviteOnly = await CreateEventAsync("Invite only");
        var noRow = await CreateEventAsync("No extension row");
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            db.GameNightEventExtensions.AddRange(
                new GameNightEventExtension { EventId = publicEvent },
                new GameNightEventExtension { EventId = restricted, Visibility = GameNightsVisibility.Restricted },
                new GameNightEventExtension { EventId = inviteOnly, Visibility = GameNightsVisibility.InviteOnly });
            db.GameNightInvites.AddRange(
                new GameNightInvite { Scope = GameNightInviteScope.Event, ScopeId = restricted, UserId = Invited },
                new GameNightInvite { Scope = GameNightInviteScope.Event, ScopeId = inviteOnly, UserId = Invited });
            await db.SaveChangesAsync();
        }

        await using var read = fixture.Factory.Services.CreateAsyncScope();
        var context = read.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        foreach (var tier in Enum.GetValues<GameNightsTier>())
        {
            foreach (var (userId, invitee) in new[] { (NotInvited, false), (Invited, true) })
            {
                Assert.True(await VisibilityFilter.CanSeeEventAsync(context, publicEvent, userId, tier, default));
                Assert.True(await VisibilityFilter.CanSeeEventAsync(context, noRow, userId, tier, default));
                Assert.Equal(
                    VisibilityRules.CanSee(GameNightsVisibility.Restricted, tier, invitee),
                    await VisibilityFilter.CanSeeEventAsync(context, restricted, userId, tier, default));
                Assert.Equal(
                    VisibilityRules.CanSee(GameNightsVisibility.InviteOnly, tier, invitee),
                    await VisibilityFilter.CanSeeEventAsync(context, inviteOnly, userId, tier, default));
            }
        }

        // The IQueryable form returns exactly the visible rows.
        var visibleToStranger = await context.GameNightEventExtensions
            .VisibleTo(context, NotInvited, GameNightsTier.Member)
            .Select(x => x.EventId).ToListAsync();
        Assert.Equal([publicEvent], visibleToStranger);
        var visibleToInvitee = await context.GameNightEventExtensions
            .VisibleTo(context, Invited, GameNightsTier.Member)
            .Select(x => x.EventId).ToListAsync();
        Assert.Equal(3, visibleToInvitee.Count);
    }

    [Fact]
    public async Task Deleting_an_event_removes_its_extension_row()
    {
        var eventId = await CreateEventAsync("Doomed");
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            db.GameNightEventExtensions.Add(new GameNightEventExtension { EventId = eventId });
            await db.SaveChangesAsync();
        }

        var deleted = await fixture.Client.DeleteAsync($"/events/{eventId}");
        deleted.EnsureSuccessStatusCode();

        await using var read = fixture.Factory.Services.CreateAsyncScope();
        var context = read.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        Assert.False(await context.GameNightEventExtensions.AnyAsync(x => x.EventId == eventId));
    }

    private async Task<Guid> CreateEventAsync(string title)
    {
        var response = await fixture.Client.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(900099, title, "in 3 hours", 52001));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventDto>())!.Id;
    }
}
