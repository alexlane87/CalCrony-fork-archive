using System.Text;
using CalCrony.Contracts.GameNights;
using Discord;

namespace CalCrony.Bot.GameNights;

/// <summary>What the reporter sees at one step. Pure builders over a <see cref="ReportSession"/>, so every custom id,
/// option list and row budget is pinned by tests that play the whole flow for each game. Limits respected by
/// construction: at most five rows, at most 25 options per select (pick-lists over 25 are refused up front by
/// <see cref="ReportFlow.IsReportable"/>), at most five inputs per form, custom ids under 100 characters.</summary>
public static class ReportView
{
    /// <summary>Custom-id prefix of every component in the flow: <c>gnrep:{session}:{action}[:{arg}...]</c>.</summary>
    public const string Prefix = "gnrep";

    /// <summary>Custom-id prefix of a form: <c>gnrepm:{session}:{step}</c>.</summary>
    public const string FormPrefix = "gnrepm";

    /// <summary>Custom-id prefix of the Report game button in a table's thread: <c>gnrepstart:{table}</c>.</summary>
    public const string StartPrefix = "gnrepstart";

    /// <summary>The select value that clears an optional table-wide answer.</summary>
    public const string SkipValue = "__skip";

    /// <summary>Renders the current step.</summary>
    /// <param name="session">The session.</param>
    /// <param name="notice">A line to show above the step (a problem, or what just happened).</param>
    /// <returns>The text, the components and (for the summary) nothing more: forms open from a button.</returns>
    public static (string Text, MessageComponent Components) Render(ReportSession session, string? notice = null)
    {
        var step = session.Current;
        var header = $"**{session.Game.Name}**: step {session.StepIndex + 1} of {session.Steps.Count}: {step.Title}" +
                     (step.Required ? "" : " *(optional)*") + (notice is null ? "" : $"\n⚠️ {notice}");
        var components = new ComponentBuilder();
        string text;
        switch (step.Kind)
        {
            case ReportStepKind.Players:
                text = $"{header}\nChoose everyone who played ({session.Game.MinSize} to {Math.Min(session.Game.MaxSize, 25)}).\n" +
                       $"Now: {(session.Players.Count == 0 ? "nobody yet" : string.Join(", ", session.Players.Select(session.Label)))}";
                PlayersRows(session, components);
                break;
            case ReportStepKind.Places:
                text = $"{header}\n{PlacesHelp(session)}";
                PlacesRows(session, components);
                break;
            case ReportStepKind.PlayerPick:
                text = $"{header}\nChoose for each player.";
                PlayerPickRows(session, step, components);
                break;
            case ReportStepKind.TablePick:
                text = header;
                TablePickRows(session, step, components);
                break;
            case ReportStepKind.Inputs:
                text = $"{header}\n{string.Join("\n", step.Items!.Select(i => $"• {i.Label}{(session.ValueOf(i) is { } v ? $": **{v}**" : "")}"))}";
                InputsRows(session, step, components);
                break;
            case ReportStepKind.Photo:
                text = $"{header}\nPlease post a photo of the final board **in this thread** so the table can check the result. " +
                       "I don't download it or keep it: it just stays here in Discord." +
                       (session.Game.PhotoRequired ? "\nThis game needs the photo before you can submit." : "\nYou can skip it.");
                PhotoRows(session, components);
                break;
            default:
                text = $"{header}\n{Summary(session)}";
                SummaryRows(session, components);
                break;
        }

        return (text, components.Build());
    }

    /// <summary>The form for a typed-answer step. Up to five inputs, each filled with what is already answered.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The form.</returns>
    public static ModalBuilder Form(ReportSession session)
    {
        var step = session.Current;
        var modal = new ModalBuilder().WithTitle(Trim(step.Title, 45)).WithCustomId($"{FormPrefix}:{session.Id}:{session.StepIndex}");
        var items = step.Items ?? [];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            modal.AddTextInput(
                label: Trim(item.Label, 45), customId: $"i{i}", style: TextInputStyle.Short, required: item.Required,
                placeholder: item.IsNumber ? "a whole number" : null, value: session.ValueOf(item), maxLength: item.IsNumber ? 12 : 200);
        }

        return modal;
    }

    /// <summary>The words for the summary step: who finished where, with their answers, then anything the API would refuse.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The text.</returns>
    public static string Summary(ReportSession session)
    {
        var text = new StringBuilder();
        var perPlayer = session.Game.Fields.Where(f => f.Kind != GameFieldKind.Player && f.Scope == GameFieldScope.Player).ToList();
        foreach (var id in session.Players.OrderBy(p => session.Places.TryGetValue(p, out var place) ? place : int.MaxValue))
        {
            var place = session.Places.TryGetValue(id, out var p) ? PlaceWord(session.Game.ResultType, p) : "";
            var answers = string.Join(", ", perPlayer
                .Select(f => (f.Label, Value: session.PlayerValues.GetValueOrDefault(f.Key)?.GetValueOrDefault(id)))
                .Where(x => x.Value is not null).Select(x => $"{x.Label}: {x.Value}"));
            text.AppendLine($"{place} **{session.Label(id)}**{(answers.Length == 0 ? "" : $" ({answers})")}".Trim());
        }

        foreach (var field in session.Game.Fields.Where(f => f.Kind != GameFieldKind.Player && f.Scope == GameFieldScope.Table))
        {
            if (session.TableValues.TryGetValue(field.Key, out var value))
            {
                text.AppendLine($"{field.Label}: {value}");
            }
        }

        text.AppendLine(session.PhotoDone ? "📷 Photo posted in the thread." : session.Game.PhotoRequired ? "📷 Photo: **still needed**." : "📷 Photo: skipped.");
        foreach (var problem in ReportFlow.Problems(session))
        {
            text.AppendLine($"⚠️ {problem}");
        }

        return text.ToString().TrimEnd();
    }

    // ---- rows ----

    private static void PlayersRows(ReportSession session, ComponentBuilder components)
    {
        components.WithSelectMenu(new SelectMenuBuilder()
            .WithCustomId($"{Prefix}:{session.Id}:pl").WithType(ComponentType.UserSelect)
            .WithPlaceholder("Choose the players")
            .WithMinValues(1).WithMaxValues(Math.Min(session.Game.MaxSize, 25)), row: 0);
        if (session.Seated.Count > 0)
        {
            components.WithButton($"Use the {session.Seated.Count} seated", $"{Prefix}:{session.Id}:seated", ButtonStyle.Secondary, row: 1,
                disabled: session.Seated.Count < session.Game.MinSize || session.Seated.Count > Math.Min(session.Game.MaxSize, 25));
        }

        components.WithButton("Next ›", $"{Prefix}:{session.Id}:next", ButtonStyle.Primary, row: 1);
    }

    private static string PlacesHelp(ReportSession session) => session.Game.ResultType switch
    {
        GameResultType.SingleWinner => "Choose the winner.",
        GameResultType.WinningSide => "Choose the winning side (1) or the other side (2) for each player.",
        _ => "Choose each player's finishing place. Players who tied share a place.",
    };

    private static void PlacesRows(ReportSession session, ComponentBuilder components)
    {
        if (session.Game.ResultType == GameResultType.SingleWinner)
        {
            var winner = session.Places.FirstOrDefault(kv => kv.Value == 1).Key;
            var select = new SelectMenuBuilder().WithCustomId($"{Prefix}:{session.Id}:win").WithPlaceholder("Who won?");
            foreach (var id in session.Players)
            {
                select.AddOption(Trim(session.Label(id), 100), id.ToString(), isDefault: winner == id);
            }

            components.WithSelectMenu(select, row: 0);
            StepNav(session, components, row: 1);
            return;
        }

        var optionCount = session.Game.ResultType == GameResultType.WinningSide ? 2 : session.Players.Count;
        PagedSelects(session, components, id =>
        {
            var select = new SelectMenuBuilder().WithCustomId($"{Prefix}:{session.Id}:pp:{id}").WithPlaceholder($"{Trim(session.Label(id), 60)}: place");
            session.Places.TryGetValue(id, out var current);
            var has = session.Places.ContainsKey(id);
            for (var n = 1; n <= optionCount; n++)
            {
                var word = session.Game.ResultType == GameResultType.WinningSide ? (n == 1 ? "winning side" : "other side") : Ordinal(n);
                select.AddOption($"{Trim(session.Label(id), 40)}: {word}", n.ToString(), isDefault: has && current == n);
            }

            return select;
        });
    }

    private static void PlayerPickRows(ReportSession session, ReportStep step, ComponentBuilder components)
    {
        var field = session.Game.Fields.Single(f => f.Key == step.FieldKey);
        PagedSelects(session, components, id =>
        {
            var select = new SelectMenuBuilder().WithCustomId($"{Prefix}:{session.Id}:pk:{FieldIndex(session, field.Key)}:{id}")
                .WithPlaceholder($"{Trim(session.Label(id), 60)}: {Trim(field.Label.ToLowerInvariant(), 30)}");
            var chosen = session.PlayerValues.GetValueOrDefault(field.Key)?.GetValueOrDefault(id);
            foreach (var value in field.Values ?? [])
            {
                select.AddOption($"{Trim(session.Label(id), 30)}: {Trim(value, 60)}", value, isDefault: chosen == value);
            }

            return select;
        });
    }

    private static void TablePickRows(ReportSession session, ReportStep step, ComponentBuilder components)
    {
        var field = session.Game.Fields.Single(f => f.Key == step.FieldKey);
        var chosen = session.TableValues.GetValueOrDefault(field.Key);
        var select = new SelectMenuBuilder().WithCustomId($"{Prefix}:{session.Id}:tp:{FieldIndex(session, field.Key)}").WithPlaceholder(field.Label);
        foreach (var value in (field.Values ?? []).Take(step.Required ? 25 : 24))
        {
            select.AddOption(Trim(value, 100), value, isDefault: chosen == value);
        }

        if (!step.Required)
        {
            select.AddOption("Not recorded", SkipValue);
        }

        components.WithSelectMenu(select, row: 0);
        StepNav(session, components, row: 1);
    }

    private static void InputsRows(ReportSession session, ReportStep step, ComponentBuilder components)
    {
        var filled = step.Items!.Count(i => !string.IsNullOrWhiteSpace(session.ValueOf(i)));
        components.WithButton(filled == 0 ? "Fill in the form" : "Edit the form", $"{Prefix}:{session.Id}:form", ButtonStyle.Primary, row: 0);
        StepNav(session, components, row: 1);
    }

    private static void PhotoRows(ReportSession session, ComponentBuilder components)
    {
        components.WithButton("I've posted it", $"{Prefix}:{session.Id}:photo", session.PhotoDone ? ButtonStyle.Success : ButtonStyle.Primary, new Emoji("📷"), row: 0);
        StepNav(session, components, row: 1);
    }

    private static void SummaryRows(ReportSession session, ComponentBuilder components)
    {
        components.WithButton("✅ Submit", $"{Prefix}:{session.Id}:submit", ButtonStyle.Success, row: 0,
            disabled: ReportFlow.Problems(session).Count > 0);
        components.WithButton("✏️ Edit", $"{Prefix}:{session.Id}:edit", ButtonStyle.Secondary, row: 0);
        components.WithButton("‹ Back", $"{Prefix}:{session.Id}:back", ButtonStyle.Secondary, row: 0);
    }

    /// <summary>Selects for the players, three to a page, then (when there is more than one page) page buttons and the step buttons.</summary>
    private static void PagedSelects(ReportSession session, ComponentBuilder components, Func<long, SelectMenuBuilder> select)
    {
        var pages = Math.Max(1, (session.Players.Count + ReportSession.PlayersPerPage - 1) / ReportSession.PlayersPerPage);
        var page = Math.Clamp(session.Page, 0, pages - 1);
        var shown = session.Players.Skip(page * ReportSession.PlayersPerPage).Take(ReportSession.PlayersPerPage).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            components.WithSelectMenu(select(shown[i]), row: i);
        }

        if (pages > 1)
        {
            components.WithButton("‹ Players", $"{Prefix}:{session.Id}:page:{page - 1}", ButtonStyle.Secondary, disabled: page == 0, row: 3);
            components.WithButton($"Page {page + 1}/{pages}", $"{Prefix}:{session.Id}:pagelabel", ButtonStyle.Secondary, disabled: true, row: 3);
            components.WithButton("Players ›", $"{Prefix}:{session.Id}:page:{page + 1}", ButtonStyle.Secondary, disabled: page == pages - 1, row: 3);
        }

        StepNav(session, components, row: 4);
    }

    private static void StepNav(ReportSession session, ComponentBuilder components, int row)
    {
        var step = session.Current;
        components.WithButton("‹ Back", $"{Prefix}:{session.Id}:back", ButtonStyle.Secondary, row: row, disabled: session.StepIndex == 0);
        if (!step.Required)
        {
            components.WithButton("Skip", $"{Prefix}:{session.Id}:skip", ButtonStyle.Secondary, row: row);
        }

        components.WithButton("Next ›", $"{Prefix}:{session.Id}:next", ButtonStyle.Primary, row: row);
    }

    private static int FieldIndex(ReportSession session, string key) => session.Game.Fields.ToList().FindIndex(f => f.Key == key);

    private static string PlaceWord(GameResultType type, int place) => type switch
    {
        GameResultType.WinningSide => place == 1 ? "🏆" : "·",
        GameResultType.SingleWinner => place == 1 ? "🏆" : "·",
        _ => $"{Ordinal(place)}",
    };

    private static string Ordinal(int n) => n switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => $"{n}th",
    };

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
