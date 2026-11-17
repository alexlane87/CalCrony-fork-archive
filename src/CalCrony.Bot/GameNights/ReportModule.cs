using CalCrony.Contracts.GameNights;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace CalCrony.Bot.GameNights;

/// <summary>Opens a report for a table: checks the table is open, that its game can be reported through Discord and that
/// the reporter is at the table (or an organiser), and starts the session with the seated players already chosen.</summary>
/// <param name="gameNights">The Game Nights API client.</param>
/// <param name="sessions">The reports in progress.</param>
/// <param name="tiers">The bot-side tier lookup.</param>
public sealed class ReportStarter(GameNightsApiClient gameNights, ReportSessionStore sessions, BotTier tiers)
{
    /// <summary>Starts a report.</summary>
    /// <param name="user">The member pressing Report game.</param>
    /// <param name="tableId">The table.</param>
    /// <returns>The first screen, or the reason it cannot start.</returns>
    public async Task<(string? Error, string Text, MessageComponent? Components)> StartAsync(SocketGuildUser user, Guid tableId)
    {
        var guildId = (long)user.Guild.Id;
        var found = await gameNights.GetTableAsync(guildId, tableId);
        if (!found.Success || found.Value is null)
        {
            return (found.NotFound ? "That table doesn't exist, or you can't see it." : $"❌ {found.Error}", "", null);
        }

        var table = found.Value;
        if (table.Status != TableStatus.Open)
        {
            return (table.Status == TableStatus.Played ? "This table already has a confirmed result." : "This table is closed.", "", null);
        }

        var game = await gameNights.GetGameAsync(guildId, table.GameId);
        if (!game.Success || game.Value is null)
        {
            return ($"❌ {game.Error}", "", null);
        }

        if (!ReportFlow.IsReportable(game.Value.Spec))
        {
            return ($"**{game.Value.Spec.Name}** is too big to report through Discord. Report it on the website.", "", null);
        }

        if (table.Seats.All(s => s.UserId != (long)user.Id) && await tiers.ResolveAsync(user) < GameNightsTier.Organiser)
        {
            return ("Only people at this table, or an organiser, can report its result.", "", null);
        }

        var seated = table.Seats.OrderBy(s => s.SeatOrder).Select(s => s.UserId).ToList();
        var session = ReportFlow.Start(user.Id, user.Guild.Id, table.Id, game.Value.Spec, seated, Labels(user.Guild, seated));
        sessions.Add(session);
        var (text, components) = ReportView.Render(session);
        return (null, text, components);
    }

    /// <summary>What the reporter sees for each person: their server display name (private to the reporter).</summary>
    /// <param name="guild">The server.</param>
    /// <param name="ids">The people.</param>
    /// <returns>The names.</returns>
    public static Dictionary<long, string> Labels(SocketGuild guild, IEnumerable<long> ids) =>
        ids.Distinct().ToDictionary(id => id, id => guild.GetUser((ulong)id)?.DisplayName ?? $"player {id}");
}

/// <summary>The Discord side of reporting a result: the Report game button, the steps (selects, a form for typed
/// answers, the photo prompt, the summary), and the Confirm and Reject buttons the others press. Everything is private
/// to the person pressing (ephemeral) until the result is submitted; the public post about it comes from the outbox.
/// All the rules live in <see cref="ReportFlow"/>; this only translates interactions into calls on it and redraws.</summary>
/// <param name="gameNights">The Game Nights API client.</param>
/// <param name="sessions">The reports in progress.</param>
/// <param name="starter">Starts a report.</param>
[RequireBotInGuild]
[RequireGameNights]
public class ReportModule(GameNightsApiClient gameNights, ReportSessionStore sessions, ReportStarter starter)
    : InteractionModuleBase<SocketInteractionContext>
{
    private const string Expired = "That report has expired. Press Report game again.";

    /// <summary>Report game pressed in a table thread.</summary>
    /// <param name="tableRaw">The table id.</param>
    [ComponentInteraction($"{ReportView.StartPrefix}:*")]
    public async Task StartAsync(string tableRaw)
    {
        await DeferAsync(ephemeral: true);
        if (Context.User is not SocketGuildUser user || !Guid.TryParse(tableRaw, out var tableId))
        {
            await FollowupAsync("That button is out of date.", ephemeral: true);
            return;
        }

        var (error, text, components) = await starter.StartAsync(user, tableId);
        if (error is not null)
        {
            await FollowupAsync(error, ephemeral: true);
            return;
        }

        await FollowupAsync(text, components: components, ephemeral: true);
    }

    /// <summary>The player select changed.</summary>
    /// <param name="sid">The session id.</param>
    /// <param name="users">The players chosen.</param>
    [ComponentInteraction("gnrep:*:pl")]
    public Task PlayersAsync(string sid, IUser[] users) => ActAsync(sid, s =>
    {
        ReportFlow.SetPlayers(s, users.Select(u => (long)u.Id), ReportStarter.Labels(Context.Guild, users.Select(u => (long)u.Id)));
        return null;
    });

    /// <summary>"Use the seated players".</summary>
    /// <param name="sid">The session id.</param>
    [ComponentInteraction("gnrep:*:seated")]
    public Task SeatedAsync(string sid) => ActAsync(sid, s =>
    {
        ReportFlow.SetPlayers(s, s.Seated, ReportStarter.Labels(Context.Guild, s.Seated));
        return null;
    });

    /// <summary>A player's finishing place (or side).</summary>
    /// <param name="sid">The session id.</param>
    /// <param name="userRaw">The player.</param>
    /// <param name="values">The place chosen.</param>
    [ComponentInteraction("gnrep:*:pp:*")]
    public Task PlaceAsync(string sid, string userRaw, string[] values) => ActAsync(sid, s =>
    {
        if (long.TryParse(userRaw, out var user) && int.TryParse(values.FirstOrDefault(), out var place))
        {
            ReportFlow.SetPlace(s, user, place);
        }

        return null;
    });

    /// <summary>The winner of a single-winner game.</summary>
    /// <param name="sid">The session id.</param>
    /// <param name="values">The winner chosen.</param>
    [ComponentInteraction("gnrep:*:win")]
    public Task WinnerAsync(string sid, string[] values) => ActAsync(sid, s =>
    {
        if (long.TryParse(values.FirstOrDefault(), out var user))
        {
            ReportFlow.SetWinner(s, user);
        }

        return null;
    });

    /// <summary>A per-player pick-list answer.</summary>
    /// <param name="sid">The session id.</param>
    /// <param name="fieldIdx">The question's position in the game's list.</param>
    /// <param name="userRaw">The player.</param>
    /// <param name="values">The choice.</param>
    [ComponentInteraction("gnrep:*:pk:*:*")]
    public Task PlayerPickAsync(string sid, string fieldIdx, string userRaw, string[] values) => ActAsync(sid, s =>
    {
        if (int.TryParse(fieldIdx, out var index) && index >= 0 && index < s.Game.Fields.Count && long.TryParse(userRaw, out var user))
        {
            ReportFlow.SetPlayerPick(s, s.Game.Fields[index].Key, user, values.FirstOrDefault());
        }

        return null;
    });

    /// <summary>A table-wide pick-list answer.</summary>
    /// <param name="sid">The session id.</param>
    /// <param name="fieldIdx">The question's position in the game's list.</param>
    /// <param name="values">The choice.</param>
    [ComponentInteraction("gnrep:*:tp:*")]
    public Task TablePickAsync(string sid, string fieldIdx, string[] values) => ActAsync(sid, s =>
    {
        if (int.TryParse(fieldIdx, out var index) && index >= 0 && index < s.Game.Fields.Count)
        {
            var choice = values.FirstOrDefault();
            ReportFlow.SetTablePick(s, s.Game.Fields[index].Key, choice == ReportView.SkipValue ? null : choice);
        }

        return null;
    });

    /// <summary>A page button within a step.</summary>
    /// <param name="sid">The session id.</param>
    /// <param name="page">The page.</param>
    [ComponentInteraction("gnrep:*:page:*")]
    public Task PageAsync(string sid, string page) => ActAsync(sid, s =>
    {
        s.Page = int.TryParse(page, out var p) ? Math.Max(0, p) : 0;
        return null;
    });

    /// <summary>Next ›.</summary>
    /// <param name="sid">The session id.</param>
    [ComponentInteraction("gnrep:*:next")]
    public Task NextAsync(string sid) => ActAsync(sid, ReportFlow.Next);

    /// <summary>‹ Back.</summary>
    /// <param name="sid">The session id.</param>
    [ComponentInteraction("gnrep:*:back")]
    public Task BackAsync(string sid) => ActAsync(sid, s =>
    {
        ReportFlow.Back(s);
        return null;
    });

    /// <summary>Skip an optional step.</summary>
    /// <param name="sid">The session id.</param>
    [ComponentInteraction("gnrep:*:skip")]
    public Task SkipAsync(string sid) => ActAsync(sid, ReportFlow.Skip);

    /// <summary>"I've posted it": the board photo is in the thread. Not checked (the bot cannot read attachments and never
    /// downloads or keeps one); it is the reporter's word.</summary>
    /// <param name="sid">The session id.</param>
    [ComponentInteraction("gnrep:*:photo")]
    public Task PhotoAsync(string sid) => ActAsync(sid, s =>
    {
        ReportFlow.AcknowledgePhoto(s);
        return null;
    });

    /// <summary>✏️ Edit from the summary.</summary>
    /// <param name="sid">The session id.</param>
    [ComponentInteraction("gnrep:*:edit")]
    public Task EditAsync(string sid) => ActAsync(sid, s =>
    {
        ReportFlow.Edit(s);
        return null;
    });

    /// <summary>Opens the typed-answers form. A form must be the first response, so there is no defer before it.</summary>
    /// <param name="sid">The session id.</param>
    [ComponentInteraction("gnrep:*:form")]
    public async Task FormAsync(string sid)
    {
        var session = Session(sid);
        if (session is null || session.Current.Kind != ReportStepKind.Inputs)
        {
            await RespondAsync(Expired, ephemeral: true);
            return;
        }

        await RespondWithModalAsync(ReportView.Form(session).Build());
    }

    /// <summary>The form was submitted.</summary>
    /// <param name="sid">The session id.</param>
    /// <param name="stepRaw">The step the form belongs to.</param>
    [ModalInteraction("gnrepm:*:*")]
    public async Task FormSubmittedAsync(string sid, string stepRaw)
    {
        await DeferAsync();
        var session = Session(sid);
        if (session is null)
        {
            await FollowupAsync(Expired, ephemeral: true);
            return;
        }

        var step = session.Current;
        string? notice = null;
        if (step.Kind == ReportStepKind.Inputs && int.TryParse(stepRaw, out var index) && index == session.StepIndex)
        {
            var answers = new Dictionary<int, string>();
            foreach (var component in ((SocketModal)Context.Interaction).Data.Components)
            {
                if (component.CustomId.StartsWith('i') && int.TryParse(component.CustomId[1..], out var position))
                {
                    answers[position] = component.Value ?? "";
                }
            }

            notice = ReportFlow.ApplyInputs(session, step, answers);
        }

        await RedrawAsync(session, notice);
    }

    /// <summary>✅ Submit: checks everything once more, then reports the result.</summary>
    /// <param name="sid">The session id.</param>
    [ComponentInteraction("gnrep:*:submit")]
    public async Task SubmitAsync(string sid)
    {
        await DeferAsync();
        var session = Session(sid);
        if (session is null)
        {
            await FollowupAsync(Expired, ephemeral: true);
            return;
        }

        if (ReportFlow.Problems(session) is { Count: > 0 } problems)
        {
            await RedrawAsync(session, string.Join(" ", problems));
            return;
        }

        var result = await gameNights.SubmitResultAsync((long)Context.Guild.Id, session.TableId, ReportFlow.ToRequest(session));
        if (!result.Success || result.Value is null)
        {
            await RedrawAsync(session, $"❌ {result.Error}");
            return;
        }

        sessions.Remove(session.Id);
        await ModifyOriginalResponseAsync(m =>
        {
            m.Content = result.Value.Status == ResultStatus.Confirmed
                ? "✅ Reported and confirmed. It's posted in the thread."
                : "✅ Reported. I've asked the others at the table to confirm it in the thread.";
            m.Components = new ComponentBuilder().Build();
        });
    }

    /// <summary>Confirm or Reject on the result post.</summary>
    /// <param name="action">"ok" or "no".</param>
    /// <param name="resultRaw">The result id.</param>
    [ComponentInteraction("gnres:*:*")]
    public async Task AnswerAsync(string action, string resultRaw)
    {
        await DeferAsync(ephemeral: true);
        if (!Guid.TryParse(resultRaw, out var resultId) || action is not ("ok" or "no"))
        {
            await FollowupAsync("That button is out of date.", ephemeral: true);
            return;
        }

        var answered = await gameNights.AnswerResultAsync((long)Context.Guild.Id, resultId, confirm: action == "ok", (long)Context.User.Id);
        await FollowupAsync(
            !answered.Success ? $"❌ {answered.Error}"
            : action == "ok" ? (answered.Value!.Status == ResultStatus.Confirmed ? "✅ Confirmed. That was the last one." : "✅ Thanks, you confirmed it.")
            : "⚠️ You rejected it. I've told the organisers, and a moderator will settle it.",
            ephemeral: true);
    }

    // ---- helpers ----

    private async Task ActAsync(string sid, Func<ReportSession, string?> act)
    {
        await DeferAsync();
        var session = Session(sid);
        if (session is null)
        {
            await FollowupAsync(Expired, ephemeral: true);
            return;
        }

        await RedrawAsync(session, act(session));
    }

    private ReportSession? Session(string sid) => Guid.TryParse(sid, out var id) ? sessions.Get(id, Context.User.Id) : null;

    private Task RedrawAsync(ReportSession session, string? notice)
    {
        var (text, components) = ReportView.Render(session, notice);
        return ModifyOriginalResponseAsync(m => { m.Content = text; m.Components = components; });
    }
}
