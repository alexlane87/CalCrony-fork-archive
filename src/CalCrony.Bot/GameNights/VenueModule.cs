using CalCrony.Bot.Api;
using CalCrony.Contracts.GameNights;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace CalCrony.Bot.GameNights;

/// <summary>Fails a Game Nights interaction when the feature flag is off, so a deployment that has
/// not opted in answers politely instead of reaching an API with no Game Nights routes.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireGameNightsAttribute : PreconditionAttribute
{
    /// <inheritdoc />
    public override Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context, ICommandInfo commandInfo, IServiceProvider services)
    {
        var config = (IConfiguration?)services.GetService(typeof(IConfiguration));
        return Task.FromResult(config?.IsGameNightsEnabled() == true
            ? PreconditionResult.FromSuccess()
            : PreconditionResult.FromError("Game Nights isn't switched on for this bot."));
    }
}

/// <summary>/venue: organiser tools for an event's venue. The venue itself is chosen on the
/// website; here an organiser opens or closes the venue's extra areas and sees the resulting limit.</summary>
/// <param name="api">The CalCrony API client.</param>
/// <param name="gameNights">The Game Nights API client.</param>
/// <param name="tiers">The bot-side tier lookup.</param>
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
[RequireBotInGuild]
[RequireGameNights]
[Group("venue", "Venue tools for game nights")]
public class VenueModule(CalCronyApiClient api, GameNightsApiClient gameNights, BotTier tiers)
    : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Shows an event's venue and a button per area to switch it on or off.</summary>
    /// <param name="name">The event (autocomplete picks it by id).</param>
    [SlashCommand("areas", "Open or close a venue's extra areas for an event (organisers)")]
    public async Task AreasAsync(
        [Summary("event", "The event"), Autocomplete(typeof(EventNameAutocompleteHandler))] string name)
    {
        await DeferAsync(ephemeral: true);

        if (Context.User is not SocketGuildUser member
            || await tiers.ResolveAsync(member) < GameNightsTier.Organiser)
        {
            await FollowupAsync("Only organisers can change a venue's areas.", ephemeral: true);
            return;
        }

        var (ev, problem) = await EventFinder.FindSingleAsync(api, (long)Context.Guild.Id, name);
        if (ev is null)
        {
            await FollowupAsync(problem ?? "I couldn't find that event.", ephemeral: true);
            return;
        }

        var venue = await gameNights.GetEventVenueAsync((long)Context.Guild.Id, ev.Id);
        if (!venue.Success || venue.Value is null)
        {
            await FollowupAsync($"❌ {venue.Error}", ephemeral: true);
            return;
        }

        if (venue.Value.Venue is null)
        {
            await FollowupAsync($"**{ev.Title}** has no venue yet. Pick one on the website's event page.", ephemeral: true);
            return;
        }

        var (embed, components) = VenuePanel.Build(ev.Title, venue.Value);
        await FollowupAsync(embed: embed, components: components, ephemeral: true);
    }
}

/// <summary>Handles the area buttons on the /venue areas panel.</summary>
/// <param name="api">The CalCrony API client.</param>
/// <param name="gameNights">The Game Nights API client.</param>
/// <param name="tiers">The bot-side tier lookup.</param>
[RequireBotInGuild]
[RequireGameNights]
public class VenueComponentModule(CalCronyApiClient api, GameNightsApiClient gameNights, BotTier tiers)
    : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Switches an area on or off and redraws the panel with the new limit.</summary>
    /// <param name="eventId">The event id.</param>
    /// <param name="areaId">The area id.</param>
    /// <param name="want">"1" to switch on, "0" to switch off.</param>
    [ComponentInteraction($"{VenuePanel.AreaButtonPrefix}:*:*:*")]
    public async Task ToggleAsync(string eventId, string areaId, string want)
    {
        await DeferAsync();

        if (Context.User is not SocketGuildUser member
            || await tiers.ResolveAsync(member) < GameNightsTier.Organiser)
        {
            await FollowupAsync("Only organisers can change a venue's areas.", ephemeral: true);
            return;
        }

        if (!Guid.TryParse(eventId, out var eventGuid) || !Guid.TryParse(areaId, out var areaGuid))
        {
            await FollowupAsync("That button is out of date. Run /venue areas again.", ephemeral: true);
            return;
        }

        var guildId = (long)Context.Guild.Id;
        var result = await gameNights.SetEventAreaAsync(guildId, eventGuid, areaGuid, want == "1");
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        var ev = await api.GetEventAsync(eventGuid);
        var (embed, components) = VenuePanel.Build(ev.Value?.Title ?? "Event", result.Value);
        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed;
            m.Components = components ?? new ComponentBuilder().Build();
        });
    }
}
