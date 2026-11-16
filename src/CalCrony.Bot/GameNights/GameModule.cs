using System.Globalization;
using CalCrony.Bot.Api;
using CalCrony.Contracts.GameNights;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace CalCrony.Bot.GameNights;

/// <summary>The first step of /game add: the basics that fit in one form (Discord allows five inputs).</summary>
public class GameBasicsModal : IModal
{
    /// <summary>The form's title.</summary>
    public string Title => "Add a game";

    /// <summary>The game's name.</summary>
    [InputLabel("Name")]
    [ModalTextInput("name", maxLength: GameRules.NameLength, placeholder: "e.g. Root")]
    public string GameName { get; set; } = "";

    /// <summary>The fewest players.</summary>
    [InputLabel("Fewest players")]
    [ModalTextInput("min", maxLength: 3, placeholder: "2")]
    public string Min { get; set; } = "2";

    /// <summary>The most players.</summary>
    [InputLabel("Most players")]
    [ModalTextInput("max", maxLength: 3, placeholder: "4")]
    public string Max { get; set; } = "4";

    /// <summary>The starting table size.</summary>
    [InputLabel("Usual table size")]
    [ModalTextInput("default", maxLength: 3, placeholder: "4")]
    public string Default { get; set; } = "4";
}

/// <summary>/game: add a game to the library step by step, follow one, post a Follow button, and mark which
/// roles are game types. Everything it changes goes through the API; the commands only talk to Discord for the
/// role itself (making it, checking the bot can give it).</summary>
/// <param name="api">The CalCrony API client.</param>
/// <param name="gameNights">The Game Nights API client.</param>
/// <param name="tiers">The bot-side tier lookup.</param>
/// <param name="follow">The follow service.</param>
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
[RequireBotInGuild]
[RequireGameNights]
[Group("game", "The game library")]
public class GameModule(GameNightsApiClient gameNights, BotTier tiers, GameFollowService follow)
    : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Starts adding a game. The form is the initial response (a modal must be), so the organiser
    /// check runs when it is submitted.</summary>
    [SlashCommand("add", "Add a game to the library, step by step (organisers)")]
    public Task AddAsync() => RespondWithModalAsync<GameBasicsModal>("gnadd:basics");

    /// <summary>Follows (or stops following) a game.</summary>
    /// <param name="game">The game (autocomplete picks it by id).</param>
    [SlashCommand("follow", "Follow a game, or stop following it")]
    public async Task FollowAsync(
        [Summary("game", "The game"), Autocomplete(typeof(GameNameAutocompleteHandler))] string game)
    {
        await DeferAsync(ephemeral: true);
        if (Context.User is not SocketGuildUser member || !Guid.TryParse(game, out var gameId))
        {
            await FollowupAsync("Pick a game from the list as you type.", ephemeral: true);
            return;
        }

        var found = await gameNights.GetGameAsync((long)Context.Guild.Id, gameId);
        if (!found.Success || found.Value is null)
        {
            await FollowupAsync(found.NotFound ? "That game isn't in the library." : $"❌ {found.Error}", ephemeral: true);
            return;
        }

        await FollowupAsync((await follow.ToggleAsync(member, found.Value)).Message, ephemeral: true);
    }

    /// <summary>Posts a public Follow button for a game in this channel.</summary>
    /// <param name="game">The game (autocomplete picks it by id).</param>
    [SlashCommand("panel", "Post a Follow button for a game in this channel (organisers)")]
    public async Task PanelAsync(
        [Summary("game", "The game"), Autocomplete(typeof(GameNameAutocompleteHandler))] string game)
    {
        await DeferAsync(ephemeral: true);
        if (!await IsAtLeastAsync(GameNightsTier.Organiser))
        {
            await FollowupAsync("Only organisers can post a Follow button.", ephemeral: true);
            return;
        }

        await PostPanelAsync(game);
    }

    /// <summary>Marks a role as a game type (a kind of game people can be interested in).</summary>
    /// <param name="role">The role.</param>
    /// <param name="label">What to call the type.</param>
    [SlashCommand("type-add", "Mark a role as a game type (admins)")]
    public async Task TypeAddAsync(
        [Summary(description: "The 'interested in' role")] IRole role,
        [Summary(description: "What to call it, e.g. Board games"), MaxLength(GameNightsLabelLimit)] string label)
    {
        await DeferAsync(ephemeral: true);
        if (!await IsAtLeastAsync(GameNightsTier.Admin))
        {
            await FollowupAsync("Only admins can choose the game types.", ephemeral: true);
            return;
        }

        var guildId = (long)Context.Guild.Id;
        var current = await gameNights.ListGameTypesAsync(guildId);
        if (!current.Success || current.Value is null)
        {
            await FollowupAsync($"❌ {current.Error}", ephemeral: true);
            return;
        }

        var next = current.Value.Where(t => t.RoleId != (long)role.Id).Append(new GameTypeDto((long)role.Id, label.Trim())).ToList();
        var saved = await gameNights.PutGameTypesAsync(guildId, next);
        await FollowupAsync(saved.Success ? $"✅ {role.Mention} is now a game type: **{label.Trim()}**." : $"❌ {saved.Error}", ephemeral: true);
    }

    /// <summary>Takes a role off the game-type list.</summary>
    /// <param name="role">The role.</param>
    [SlashCommand("type-remove", "Stop treating a role as a game type (admins)")]
    public async Task TypeRemoveAsync([Summary(description: "The role to remove")] IRole role)
    {
        await DeferAsync(ephemeral: true);
        if (!await IsAtLeastAsync(GameNightsTier.Admin))
        {
            await FollowupAsync("Only admins can choose the game types.", ephemeral: true);
            return;
        }

        var guildId = (long)Context.Guild.Id;
        var current = await gameNights.ListGameTypesAsync(guildId);
        if (!current.Success || current.Value is null)
        {
            await FollowupAsync($"❌ {current.Error}", ephemeral: true);
            return;
        }

        var saved = await gameNights.PutGameTypesAsync(guildId, [.. current.Value.Where(t => t.RoleId != (long)role.Id)]);
        await FollowupAsync(saved.Success ? $"✅ {role.Mention} is no longer a game type." : $"❌ {saved.Error}", ephemeral: true);
    }

    private const int GameNightsLabelLimit = 60;

    private async Task<bool> IsAtLeastAsync(GameNightsTier tier) =>
        Context.User is SocketGuildUser member && await tiers.ResolveAsync(member) >= tier;

    private async Task PostPanelAsync(string game)
    {
        if (!Guid.TryParse(game, out var gameId))
        {
            await FollowupAsync("Pick a game from the list as you type.", ephemeral: true);
            return;
        }

        var found = await gameNights.GetGameAsync((long)Context.Guild.Id, gameId);
        if (!found.Success || found.Value is null || found.Value.Spec.RoleId is null)
        {
            await FollowupAsync(found.Value is null ? "That game isn't in the library." : $"**{found.Value.Spec.Name}** has no follow role yet.", ephemeral: true);
            return;
        }

        var (embed, components) = GameWizardView.FollowPanel(found.Value);
        await Context.Channel.SendMessageAsync(embed: embed, components: components, allowedMentions: AllowedMentions.None);
        await FollowupAsync("Posted.", ephemeral: true);
    }
}

/// <summary>Handles the steps and buttons of /game add, and the public Follow button. Every handler that
/// changes anything re-checks who is pressing: a button in an ephemeral message is only visible to its owner,
/// but a custom id is not a credential.</summary>
/// <param name="gameNights">The Game Nights API client.</param>
/// <param name="tiers">The bot-side tier lookup.</param>
/// <param name="follow">The follow service.</param>
/// <param name="drafts">The in-progress drafts.</param>
[RequireBotInGuild]
[RequireGameNights]
public class GameWizardComponentModule(GameNightsApiClient gameNights, BotTier tiers, GameFollowService follow, GameDraftStore drafts)
    : InteractionModuleBase<SocketInteractionContext>
{
    private const string Expired = "That form has expired. Run /game add again.";

    /// <summary>Step 1 submitted: check the organiser, check the numbers, then ask how a result is recorded.</summary>
    /// <param name="modal">The submitted form.</param>
    [ModalInteraction("gnadd:basics")]
    public async Task BasicsAsync(GameBasicsModal modal)
    {
        await DeferAsync(ephemeral: true);
        if (Context.User is not SocketGuildUser member || await tiers.ResolveAsync(member) < GameNightsTier.Organiser)
        {
            await FollowupAsync("Only organisers can add games.", ephemeral: true);
            return;
        }

        if (!int.TryParse(modal.Min, NumberStyles.None, CultureInfo.InvariantCulture, out var min)
            || !int.TryParse(modal.Max, NumberStyles.None, CultureInfo.InvariantCulture, out var max)
            || !int.TryParse(modal.Default, NumberStyles.None, CultureInfo.InvariantCulture, out var usual))
        {
            await FollowupAsync("The player counts have to be whole numbers.", ephemeral: true);
            return;
        }

        var probe = GameWizard.Build(modal.GameName, GameResultType.RankedPlacement, min, max, usual, GameWizard.FieldTemplate.PlayersOnly, null, []);
        if (GameRules.Validate(probe) is { Count: > 0 } problems)
        {
            await FollowupAsync("⚠️ " + string.Join(" ", problems), ephemeral: true);
            return;
        }

        var draft = new GameDraft
        {
            OwnerId = Context.User.Id, GuildId = Context.Guild.Id, Name = modal.GameName.Trim(),
            MinSize = min, MaxSize = max, DefaultSize = Math.Clamp(usual, min, max),
        };
        drafts.Add(draft);
        var (text, components) = GameWizardView.ResultTypeStep(draft);
        await FollowupAsync(text, components: components, ephemeral: true);
    }

    /// <summary>Step 2: the result type.</summary>
    /// <param name="id">The draft id.</param>
    /// <param name="values">The chosen option.</param>
    [ComponentInteraction($"{GameWizardView.TypePrefix}:*")]
    public async Task TypeAsync(string id, string[] values)
    {
        if (Draft(id) is not { } draft || !Enum.TryParse<GameResultType>(values.FirstOrDefault(), out var type))
        {
            await RespondAsync(Expired, ephemeral: true);
            return;
        }

        draft.ResultType = type;
        var (text, components) = GameWizardView.TemplateStep(draft);
        await UpdateAsync(text, components);
    }

    /// <summary>Step 3: the starting questions.</summary>
    /// <param name="id">The draft id.</param>
    /// <param name="values">The chosen option.</param>
    [ComponentInteraction($"{GameWizardView.TemplatePrefix}:*")]
    public async Task TemplateAsync(string id, string[] values)
    {
        if (Draft(id) is not { } draft || !Enum.TryParse<GameWizard.FieldTemplate>(values.FirstOrDefault(), out var template))
        {
            await RespondAsync(Expired, ephemeral: true);
            return;
        }

        draft.Template = template;
        var (text, components) = GameWizardView.RoleStep(draft, GameRoleChecks.CannotCreate(BotCanManageRoles()));
        await UpdateAsync(text, components);
    }

    /// <summary>Step 4, attach: an existing role was picked. Refused, with the reason, when the bot could not give it out.</summary>
    /// <param name="id">The draft id.</param>
    /// <param name="roles">The role picked.</param>
    [ComponentInteraction($"{GameWizardView.RolePrefix}:*")]
    public async Task AttachRoleAsync(string id, IRole[] roles)
    {
        if (Draft(id) is not { } draft || roles.FirstOrDefault() is not { } role)
        {
            await RespondAsync(Expired, ephemeral: true);
            return;
        }

        var me = Context.Guild.CurrentUser;
        var warning = GameRoleChecks.CannotGrant(
            role.Name, me.GuildPermissions.ManageRoles, me.Hierarchy, role.Position, role.IsManaged, role.Id == Context.Guild.Id);
        if (warning is not null)
        {
            var (text, components) = GameWizardView.RoleStep(draft, GameRoleChecks.CannotCreate(BotCanManageRoles()), warning);
            await UpdateAsync(text, components);
            return;
        }

        await DeferAsync();
        await FinishAsync(draft, (long)role.Id, role.Mention);
    }

    /// <summary>Step 4, create: make a role named for the game. Refused, with the fix, when the bot lacks Manage Roles.</summary>
    /// <param name="id">The draft id.</param>
    [ComponentInteraction($"{GameWizardView.NewRolePrefix}:*")]
    public async Task NewRoleAsync(string id)
    {
        if (Draft(id) is not { } draft)
        {
            await RespondAsync(Expired, ephemeral: true);
            return;
        }

        if (GameRoleChecks.CannotCreate(BotCanManageRoles()) is { } warning)
        {
            var (text, components) = GameWizardView.RoleStep(draft, warning, "I can't make the role until that's fixed. You can still pick an existing role or go without one.");
            await UpdateAsync(text, components);
            return;
        }

        await DeferAsync();
        IRole role;
        try
        {
            role = await Context.Guild.CreateRoleAsync(draft.Name, GuildPermissions.None, isHoisted: false, isMentionable: true);
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            var (text, components) = GameWizardView.RoleStep(draft, null, "Discord refused to create the role: my role needs Manage Roles.");
            await ModifyOriginalResponseAsync(m => { m.Content = text; m.Components = components; });
            return;
        }

        await FinishAsync(draft, (long)role.Id, role.Mention);
    }

    /// <summary>Step 4, skip: no role.</summary>
    /// <param name="id">The draft id.</param>
    [ComponentInteraction($"{GameWizardView.NoRolePrefix}:*")]
    public async Task NoRoleAsync(string id)
    {
        if (Draft(id) is not { } draft)
        {
            await RespondAsync(Expired, ephemeral: true);
            return;
        }

        await DeferAsync();
        await FinishAsync(draft, null, null);
    }

    /// <summary>The "Post a Follow button here" button on the confirmation.</summary>
    /// <param name="gameIdRaw">The game id.</param>
    [ComponentInteraction($"{GameWizardView.PanelPrefix}:*")]
    public async Task PanelAsync(string gameIdRaw)
    {
        await DeferAsync(ephemeral: true);
        if (Context.User is not SocketGuildUser member || await tiers.ResolveAsync(member) < GameNightsTier.Organiser)
        {
            await FollowupAsync("Only organisers can post a Follow button.", ephemeral: true);
            return;
        }

        var found = Guid.TryParse(gameIdRaw, out var id) ? await gameNights.GetGameAsync((long)Context.Guild.Id, id) : null;
        if (found?.Value is not { Spec.RoleId: not null } game)
        {
            await FollowupAsync("That game has no follow role to post a button for.", ephemeral: true);
            return;
        }

        var (embed, components) = GameWizardView.FollowPanel(game);
        await Context.Channel.SendMessageAsync(embed: embed, components: components, allowedMentions: AllowedMentions.None);
        await FollowupAsync("Posted.", ephemeral: true);
    }

    /// <summary>The public Follow button: toggles the pressing member's follow role.</summary>
    /// <param name="gameIdRaw">The game id.</param>
    [ComponentInteraction($"{GameWizardView.FollowPrefix}:*")]
    public async Task FollowButtonAsync(string gameIdRaw)
    {
        await DeferAsync(ephemeral: true);
        if (Context.User is not SocketGuildUser member || !Guid.TryParse(gameIdRaw, out var id))
        {
            await FollowupAsync("That button is out of date.", ephemeral: true);
            return;
        }

        var found = await gameNights.GetGameAsync((long)Context.Guild.Id, id);
        if (!found.Success || found.Value is null || !found.Value.Active)
        {
            await FollowupAsync("That game isn't in the library any more.", ephemeral: true);
            return;
        }

        await FollowupAsync((await follow.ToggleAsync(member, found.Value)).Message, ephemeral: true);
    }

    /// <summary>Creates the game with what the draft holds, and the role if one was chosen or made.</summary>
    private async Task FinishAsync(GameDraft draft, long? roleId, string? roleMention)
    {
        var guildId = (long)Context.Guild.Id;
        var existing = await gameNights.ListGamesAsync(guildId, includeRetired: true);
        var spec = draft.ToSpec(roleId, existing.Value?.Select(g => g.Spec.Key) ?? []);
        if (GameRules.Validate(spec) is { Count: > 0 } problems)
        {
            await ModifyOriginalResponseAsync(m => { m.Content = "⚠️ " + string.Join(" ", problems); m.Components = new ComponentBuilder().Build(); });
            return;
        }

        var created = await gameNights.CreateGameAsync(guildId, spec);
        if (!created.Success || created.Value is null)
        {
            var (text, components) = GameWizardView.RoleStep(draft, null, $"I couldn't save the game: {created.Error}");
            await ModifyOriginalResponseAsync(m => { m.Content = text; m.Components = components; });
            return;
        }

        drafts.Remove(draft.Id);
        var (done, panel) = GameWizardView.Created(created.Value, roleMention);
        await ModifyOriginalResponseAsync(m => { m.Content = done; m.Components = panel ?? new ComponentBuilder().Build(); });
    }

    private Task UpdateAsync(string text, MessageComponent components) =>
        ((SocketMessageComponent)Context.Interaction).UpdateAsync(m => { m.Content = text; m.Components = components; });

    private GameDraft? Draft(string id) =>
        Guid.TryParse(id, out var draftId) ? drafts.Get(draftId, Context.User.Id) : null;

    private bool BotCanManageRoles() => Context.Guild.CurrentUser.GuildPermissions.ManageRoles;
}
