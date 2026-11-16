using CalCrony.Bot.GameNights;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Discord;

namespace CalCrony.Bot.Tests.GameNights;

public class GameRoleChecksTests
{
    [Fact]
    public void Creating_a_role_needs_manage_roles_and_the_warning_says_where_to_fix_it()
    {
        Assert.Null(GameRoleChecks.CannotCreate(true));

        var warning = GameRoleChecks.CannotCreate(false);

        Assert.Contains("Manage Roles", warning);
        Assert.Contains("Server Settings", warning);
    }

    [Fact]
    public void An_existing_role_can_be_given_only_when_it_sits_below_the_bots_role()
    {
        Assert.Null(GameRoleChecks.CannotGrant("Chess", true, botHighestPosition: 10, rolePosition: 3, roleIsManaged: false, roleIsEveryone: false));

        var above = GameRoleChecks.CannotGrant("Chess", true, 3, 10, false, false);
        Assert.Contains("Chess", above);
        Assert.Contains("drag my role above it", above);
        Assert.NotNull(GameRoleChecks.CannotGrant("Chess", true, 5, 5, false, false)); // level counts as not above
    }

    [Fact]
    public void Missing_permission_managed_and_everyone_roles_each_get_their_own_message()
    {
        Assert.Contains("Manage Roles", GameRoleChecks.CannotGrant("Chess", false, 10, 3, false, false));
        Assert.Contains("integration", GameRoleChecks.CannotGrant("Booster", true, 10, 3, true, false));
        Assert.Contains("@everyone", GameRoleChecks.CannotGrant("@everyone", true, 10, 0, false, true));
    }
}

public class GameWizardContractsTests
{
    [Theory]
    [InlineData("Root", new string[0], "root")]
    [InlineData("  Twilight Imperium: 4th Ed. ", new string[0], "twilight_imperium_4th_ed")]
    [InlineData("Root", new[] { "root" }, "root_2")]
    [InlineData("Root", new[] { "root", "root_2" }, "root_3")]
    [InlineData("7 Wonders", new string[0], "game_7_wonders")]
    [InlineData("!!!", new string[0], "game")]
    public void A_key_is_made_from_the_name_and_kept_unique(string name, string[] taken, string expected) =>
        Assert.Equal(expected, GameWizard.KeyFromName(name, taken));

    [Fact]
    public void A_very_long_name_still_gives_a_valid_key()
    {
        var key = GameWizard.KeyFromName(new string('a', 300), ["x"]);

        Assert.True(GameRules.IsValidKey(key));
        Assert.True(key.Length <= GameRules.KeyLength);
    }

    [Fact]
    public void The_scores_template_asks_for_a_required_score_per_player_and_players_only_does_not()
    {
        var withScores = GameWizard.Build("Chess", GameResultType.SingleWinner, 2, 2, 2, GameWizard.FieldTemplate.PlayersAndScores, 99, []);
        var plain = GameWizard.Build("Chess", GameResultType.SingleWinner, 2, 2, 2, GameWizard.FieldTemplate.PlayersOnly, null, []);

        Assert.Equal(["players", "score"], withScores.Fields.Select(f => f.Key));
        var score = withScores.Fields.Single(f => f.Key == "score");
        Assert.Equal((GameFieldKind.Number, GameFieldScope.Player, true), (score.Kind, score.Scope, score.Required));
        Assert.Equal(99L, withScores.RoleId);
        Assert.Equal(["players"], plain.Fields.Select(f => f.Key));
        Assert.Null(plain.RoleId);
        Assert.Empty(GameRules.Validate(withScores));
        Assert.Empty(GameRules.Validate(plain));
    }

    [Fact]
    public void The_usual_size_is_pulled_into_the_range_and_bad_ranges_are_left_for_validation_to_catch()
    {
        Assert.Equal(6, GameWizard.Build("G", GameResultType.RankedPlacement, 2, 6, 9, GameWizard.FieldTemplate.PlayersOnly, null, []).DefaultSize);
        var broken = GameWizard.Build("G", GameResultType.RankedPlacement, 5, 2, 3, GameWizard.FieldTemplate.PlayersOnly, null, []);
        Assert.NotEmpty(GameRules.Validate(broken));
    }
}

public class GameWizardViewTests
{
    private static GameDraft Draft() => new() { OwnerId = 1, GuildId = 2, Name = "Chess", MinSize = 2, MaxSize = 2, DefaultSize = 2 };

    [Fact]
    public void Every_step_carries_the_drafts_id_and_stays_inside_discords_limits()
    {
        var draft = Draft();

        var type = GameWizardView.ResultTypeStep(draft).Components;
        var template = GameWizardView.TemplateStep(draft).Components;
        var role = GameWizardView.RoleStep(draft, null).Components;

        foreach (var components in new[] { type, template, role })
        {
            foreach (var row in components.Components.OfType<ActionRowComponent>())
            {
                foreach (var c in row.Components)
                {
                    var id = c switch { ButtonComponent b => b.CustomId, SelectMenuComponent m => m.CustomId, _ => "" };
                    Assert.Contains(draft.Id.ToString(), id);
                    Assert.InRange(id.Length, 1, 100);
                }
            }
        }
    }

    [Fact]
    public void The_type_and_template_selects_offer_exactly_the_enum_values_the_handlers_parse()
    {
        var type = (SelectMenuComponent)GameWizardView.ResultTypeStep(Draft()).Components.Components.OfType<ActionRowComponent>().Single().Components.Single();
        var template = (SelectMenuComponent)GameWizardView.TemplateStep(Draft()).Components.Components.OfType<ActionRowComponent>().Single().Components.Single();

        Assert.Equal(Enum.GetNames<GameResultType>().Order(), type.Options.Select(o => o.Value).Order());
        Assert.Equal(Enum.GetNames<GameWizard.FieldTemplate>().Order(), template.Options.Select(o => o.Value).Order());
        Assert.All(type.Options.Concat(template.Options), o => Assert.True(o.Label.Length <= 100));
    }

    [Fact]
    public void The_role_step_offers_attach_create_and_none_and_warns_when_the_bot_cannot_make_roles()
    {
        var draft = Draft();

        var (text, components) = GameWizardView.RoleStep(draft, "I can't create roles here.", "That role sits above mine.");

        var rows = components.Components.OfType<ActionRowComponent>().ToList();
        var select = Assert.IsType<SelectMenuComponent>(rows[0].Components.Single());
        Assert.Equal(ComponentType.RoleSelect, select.Type);
        Assert.Equal(1, select.MaxValues);
        var buttons = rows[1].Components.OfType<ButtonComponent>().ToList();
        Assert.Equal(2, buttons.Count);
        Assert.Contains(buttons, b => b.CustomId.StartsWith(GameWizardView.NewRolePrefix));
        Assert.Contains(buttons, b => b.CustomId.StartsWith(GameWizardView.NoRolePrefix));
        Assert.Contains("I can't create roles here.", text);
        Assert.Contains("That role sits above mine.", text);
        Assert.DoesNotContain("⚠️", GameWizardView.RoleStep(draft, null).Text);
    }

    [Fact]
    public void The_confirmation_offers_a_follow_panel_only_when_the_game_has_a_role()
    {
        var game = new GameDto(Guid.NewGuid(), true,
            new GameSpec("chess", "Chess", GameResultType.SingleWinner, 2, 2, 2, false, 32, 1000, []));

        Assert.Null(GameWizardView.Created(game, null).Components);
        var withRole = GameWizardView.Created(game, "<@&5>");
        Assert.Contains("<@&5>", withRole.Text);
        var button = withRole.Components!.Components.OfType<ActionRowComponent>().Single().Components.OfType<ButtonComponent>().Single();
        Assert.Equal($"gnpanel:{game.Id}", button.CustomId);
    }

    [Fact]
    public void The_public_follow_panel_has_one_button_for_the_game()
    {
        var game = new GameDto(Guid.NewGuid(), true,
            new GameSpec("chess", "Chess", GameResultType.SingleWinner, 2, 2, 2, false, 32, 1000, [], 5));

        var (embed, components) = GameWizardView.FollowPanel(game);

        Assert.Contains("Chess", embed.Title);
        var button = components.Components.OfType<ActionRowComponent>().Single().Components.OfType<ButtonComponent>().Single();
        Assert.Equal($"gnfollow:{game.Id}", button.CustomId);
    }
}

public class GameDraftStoreTests
{
    private static GameDraft Draft(ulong owner = 1) => new() { OwnerId = owner, GuildId = 2, Name = "Chess", MinSize = 2, MaxSize = 2, DefaultSize = 2 };

    [Fact]
    public void Only_the_person_who_started_a_draft_can_continue_it()
    {
        var store = new GameDraftStore();
        var draft = Draft();
        store.Add(draft);

        Assert.Same(draft, store.Get(draft.Id, 1));
        Assert.Null(store.Get(draft.Id, 2));
        Assert.Null(store.Get(Guid.NewGuid(), 1));
    }

    [Fact]
    public void An_expired_draft_is_gone_and_a_finished_one_can_be_removed()
    {
        var store = new GameDraftStore();
        var stale = Draft();
        stale.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        store.Add(stale);
        Assert.Null(store.Get(stale.Id, 1));

        var done = Draft();
        store.Add(done);
        store.Remove(done.Id);
        Assert.Null(store.Get(done.Id, 1));
    }

    [Fact]
    public void A_draft_turns_into_a_definition_with_the_choices_so_far()
    {
        var draft = Draft();
        draft.ResultType = GameResultType.SingleWinner;
        draft.Template = GameWizard.FieldTemplate.PlayersOnly;

        var spec = draft.ToSpec(roleId: 42, takenKeys: ["chess"]);

        Assert.Equal(("chess_2", GameResultType.SingleWinner, 42L), (spec.Key, spec.ResultType, spec.RoleId));
        Assert.Equal(["players"], spec.Fields.Select(f => f.Key));
    }

    [Fact]
    public void The_role_handler_owns_exactly_its_two_delivery_types()
    {
        var handler = new GameRoleDeliveryHandler(null!, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<GameRoleDeliveryHandler>.Instance);

        Assert.True(handler.Handles(DeliveryType.CreateGameRole));
        Assert.True(handler.Handles(DeliveryType.GameRoleFollow));
        Assert.False(handler.Handles(DeliveryType.CreateTableThread));
    }
}
