using System.Text.Json;
using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.GameNights;

/// <summary>Result reporting. Someone at the table reports who played and how it went; the other members who played
/// confirm it (all of them, or more than half, by <c>GameNights:ConfirmMode</c>); a rejection makes it disputed and
/// tells the organisers; a moderator settles disputes and can void a result so the table reports again. Only
/// confirmed results will feed leagues.
/// <para>The report is checked against the game's own definition by the shared <see cref="ResultValidator"/>. Members are
/// stored by Discord id only (their "First L" is worked out when shown, see <see cref="NameService"/>); guests keep
/// the one typed name. There is no photo field: the board photo stays in Discord and never reaches this API.</para></summary>
public static class ResultEndpoints
{
    /// <summary>The configuration key for how many players must confirm (<see cref="ConfirmMode"/>).</summary>
    public const string ConfirmModeKey = "GameNights:ConfirmMode";

    /// <summary>Maps the result routes onto the Game Nights group.</summary>
    /// <param name="g">The <c>/guilds/{guildId}/game-nights</c> group.</param>
    public static void MapResultEndpoints(this RouteGroupBuilder g)
    {
        g.MapGet("/tables/{tableId:guid}/results", ListResults).RequireTier(GameNightsTier.Member);
        g.MapPost("/tables/{tableId:guid}/results", Submit).RequireTier(GameNightsTier.Member);
        g.MapGet("/results/{resultId:guid}", GetResult).RequireTier(GameNightsTier.Member);
        g.MapPost("/results/{resultId:guid}/confirm", (long guildId, Guid resultId, AnswerResultRequest request, HttpContext context,
            TierResolver tiers, CalCronyDbContext db, IClock clock, IConfiguration configuration, CancellationToken ct) =>
            Answer(guildId, resultId, request, ConfirmationDecision.Confirm, context, tiers, db, clock, configuration, ct))
            .RequireTier(GameNightsTier.Member);
        g.MapPost("/results/{resultId:guid}/reject", (long guildId, Guid resultId, AnswerResultRequest request, HttpContext context,
            TierResolver tiers, CalCronyDbContext db, IClock clock, IConfiguration configuration, CancellationToken ct) =>
            Answer(guildId, resultId, request, ConfirmationDecision.Reject, context, tiers, db, clock, configuration, ct))
            .RequireTier(GameNightsTier.Member);
        g.MapPost("/results/{resultId:guid}/resolve", Resolve).RequireTier(GameNightsTier.Moderator);
        g.MapPut("/results/{resultId:guid}/summary-message", SetSummaryMessage).RequireAuthorization("BotOnly").RequireTier(GameNightsTier.Member);
    }

    // ---- reads ----

    private static async Task<IResult> ListResults(
        long guildId, Guid tableId, HttpContext context, TierResolver tiers, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var (tier, userId) = await TableEndpoints.WhoAsync(context, tiers, guildId, cancellationToken);
        var table = await TableEndpoints.FindTableAsync(db, guildId, tableId, track: false, cancellationToken);
        if (table is null || !await TableEndpoints.CanSeeTableAsync(db, table, userId, tier, cancellationToken))
        {
            return Results.NotFound();
        }

        var results = await LoadResults(db).AsNoTracking().Where(r => r.TableId == tableId).OrderBy(r => r.SubmittedAt).ToListAsync(cancellationToken);
        return Results.Ok(await ToDtosAsync(db, guildId, results, null, cancellationToken));
    }

    private static async Task<IResult> GetResult(
        long guildId, Guid resultId, HttpContext context, TierResolver tiers, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var (tier, userId) = await TableEndpoints.WhoAsync(context, tiers, guildId, cancellationToken);
        var result = await FindResultAsync(db, guildId, resultId, track: false, cancellationToken);
        if (result is null || !await CanSeeAsync(db, guildId, result, userId, tier, cancellationToken))
        {
            return Results.NotFound();
        }

        return Results.Ok((await ToDtosAsync(db, guildId, [result], null, cancellationToken))[0]);
    }

    // ---- submit ----

    private static async Task<IResult> Submit(
        long guildId, Guid tableId, SubmitResultRequest request, HttpContext context, TierResolver tiers, CalCronyDbContext db,
        IClock clock, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var (tier, caller) = await TableEndpoints.WhoAsync(context, tiers, guildId, cancellationToken);
        var visible = await TableEndpoints.FindTableAsync(db, guildId, tableId, track: false, cancellationToken);
        if (visible is null || !await TableEndpoints.CanSeeTableAsync(db, visible, caller, tier, cancellationToken))
        {
            return Results.NotFound();
        }

        if (request.Submission is null)
        {
            return Bad("The report is empty.");
        }

        var reporter = context.User.IsBot() ? request.ReporterUserId : caller;
        if (reporter is not > 0)
        {
            return Bad("Say who is reporting (reporterUserId); the website always means the signed-in member.");
        }

        var players = request.Submission.Players ?? [];
        var memberIds = players.Where(p => p.UserId is not null).Select(p => p.UserId!.Value).ToList();
        if (!context.User.IsBot() && !memberIds.Contains(reporter.Value) && tier < GameNightsTier.Organiser)
        {
            return Results.Json(new ErrorResponse("Only someone who played, or an organiser, can report this table's result."), statusCode: 403);
        }

        var overrides = new Dictionary<long, string>();
        foreach (var (userId, typed) in request.NameOverrides ?? new Dictionary<long, string>())
        {
            if (DisplayNames.Tidy(typed) is not { } tidy)
            {
                return Bad($"\"{typed}\" isn't in the First L form (for example \"Alex P\").");
            }

            overrides[userId] = tidy;
        }

        var game = await db.GameNightGames.AsNoTracking().Include(g => g.Fields).FirstOrDefaultAsync(g => g.Id == visible.GameId, cancellationToken);
        if (game is null)
        {
            return Bad("This table's game is no longer in the library.");
        }

        var spec = GameCatalog.ToSpec(game);
        if (ResultValidator.Validate(spec, request.Submission) is { Count: > 0 } problems)
        {
            return Bad(string.Join(" ", problems.Select(p => p.Message)));
        }

        var guestIds = players.Where(p => p.GuestId is not null).Select(p => p.GuestId!.Value).Distinct().ToList();
        if (guestIds.Count > 0
            && await db.GameNightGuests.CountAsync(x => x.GuildId == guildId && !x.Erased && guestIds.Contains(x.Id), cancellationToken) != guestIds.Count)
        {
            return Bad("A guest on this report isn't known to this server.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"""SELECT "Id" FROM "GameNightTables" WHERE "Id" = {tableId} FOR UPDATE""", cancellationToken);
        var table = (await TableEndpoints.FindTableAsync(db, guildId, tableId, track: true, cancellationToken))!;
        if (table.Status == TableStatus.Cancelled)
        {
            return Results.Conflict(new ErrorResponse("This table was cancelled."));
        }

        var current = await db.GameNightResults.AsNoTracking()
            .Where(r => r.TableId == tableId && r.Status != ResultStatus.Voided)
            .Select(r => (ResultStatus?)r.Status).FirstOrDefaultAsync(cancellationToken);
        if (current is not null || table.Status == TableStatus.Played)
        {
            return Results.Conflict(new ErrorResponse(current switch
            {
                ResultStatus.Pending => "This table already has a result waiting to be confirmed.",
                ResultStatus.Disputed => "This table's result is disputed; a moderator has to settle it before it can be reported again.",
                _ => "This table already has a confirmed result. A moderator can void it so it can be reported again.",
            }));
        }

        var now = clock.GetCurrentInstant();
        var result = new GameNightResult
        {
            Id = Guid.NewGuid(), TableId = tableId, GameId = game.Id, Status = ResultStatus.Pending,
            ReporterUserId = reporter.Value, SubmittedAt = now,
        };
        var perPlayerKeys = spec.Fields.Where(f => f.Kind != GameFieldKind.Player && f.Scope == GameFieldScope.Player).Select(f => f.Key).ToHashSet();
        var tableKeys = spec.Fields.Where(f => f.Kind != GameFieldKind.Player && f.Scope == GameFieldScope.Table).Select(f => f.Key).ToHashSet();

        foreach (var player in players)
        {
            var values = (player.Values ?? new Dictionary<string, string>())
                .Where(kv => perPlayerKeys.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
            long? score = values.TryGetValue("score", out var raw) && long.TryParse(raw, out var parsed) ? parsed : null;
            values.Remove("score");
            result.Players.Add(new GameNightResultPlayer
            {
                Id = Guid.NewGuid(), ResultId = result.Id, UserId = player.UserId, GuestId = player.GuestId,
                Placement = player.Placement, Score = score, ValuesJson = values.Count == 0 ? null : JsonSerializer.Serialize(values),
            });
        }

        foreach (var (key, value) in (request.Submission.TableValues ?? new Dictionary<string, string>()).Where(kv => tableKeys.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value)))
        {
            result.Values.Add(new GameNightResultValue { ResultId = result.Id, Key = key, Value = value.Trim() });
        }

        if (memberIds.Contains(reporter.Value))
        {
            result.Confirmations.Add(new GameNightResultConfirmation { ResultId = result.Id, UserId = reporter.Value, Decision = ConfirmationDecision.Confirm, At = now });
        }

        SettleIfConfirmed(result, table, memberIds, now, Mode(configuration));
        db.GameNightResults.Add(result);

        // Everything the bot says about this result goes through the outbox, queued with the result itself: the post
        // for the table's thread now, and (while it is waiting) one reminder for whoever has not answered.
        var channel = await db.Events.AsNoTracking().Where(e => e.Id == table.EventId).Select(e => e.ChannelId).FirstAsync(cancellationToken);
        Notice(db, clock, DeliveryType.ResultSubmitted, channel, result, guildId, null);
        if (result.Status == ResultStatus.Pending && ReminderHours(configuration) is { } hours)
        {
            Notice(db, clock, DeliveryType.ResultReminder, channel, result, guildId, Duration.FromHours(hours));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var dto = (await ToDtosAsync(db, guildId, [result], overrides, cancellationToken))[0];
        return Results.Created($"/guilds/{guildId}/game-nights/results/{result.Id}", dto);
    }

    // ---- confirm / reject ----

    private static async Task<IResult> Answer(
        long guildId, Guid resultId, AnswerResultRequest request, ConfirmationDecision decision, HttpContext context,
        TierResolver tiers, CalCronyDbContext db, IClock clock, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var (tier, caller) = await TableEndpoints.WhoAsync(context, tiers, guildId, cancellationToken);
        var subject = context.User.IsBot() ? request.UserId : caller;
        if (subject is not > 0)
        {
            return Bad("Say whose answer this is (userId); the website always means the signed-in member.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"""SELECT "Id" FROM "GameNightResults" WHERE "Id" = {resultId} FOR UPDATE""", cancellationToken);
        var result = await FindResultAsync(db, guildId, resultId, track: true, cancellationToken);
        if (result is null || !await CanSeeAsync(db, guildId, result, caller, tier, cancellationToken))
        {
            return Results.NotFound();
        }

        var memberIds = result.Players.Where(p => p.UserId is not null).Select(p => p.UserId!.Value).ToList();
        if (!memberIds.Contains(subject.Value))
        {
            return Results.Json(new ErrorResponse("Only people who played can confirm or reject this result."), statusCode: 403);
        }

        if (result.Status is ResultStatus.Confirmed or ResultStatus.Voided
            || (result.Status == ResultStatus.Disputed && decision == ConfirmationDecision.Confirm))
        {
            return Results.Conflict(new ErrorResponse(result.Status switch
            {
                ResultStatus.Confirmed => "This result is already confirmed.",
                ResultStatus.Voided => "This result was withdrawn.",
                _ => "This result is disputed; a moderator has to settle it.",
            }));
        }

        var now = clock.GetCurrentInstant();
        var mine = result.Confirmations.FirstOrDefault(c => c.UserId == subject.Value);
        if (mine is null)
        {
            result.Confirmations.Add(new GameNightResultConfirmation { ResultId = result.Id, UserId = subject.Value, Decision = decision, At = now });
        }
        else
        {
            mine.Decision = decision;
            mine.At = now;
        }

        var table = (await TableEndpoints.FindTableAsync(db, guildId, result.TableId, track: true, cancellationToken))!;
        var statusBefore = result.Status;
        if (decision == ConfirmationDecision.Reject)
        {
            if (result.Status != ResultStatus.Disputed)
            {
                result.Status = ResultStatus.Disputed;
                var roles = await db.GameNightsTierRoles.AsNoTracking().Where(t => t.GuildId == guildId).Select(t => t.RoleId).ToListAsync(cancellationToken);
                var channel = await db.Events.AsNoTracking().Where(e => e.Id == table.EventId).Select(e => e.ChannelId).FirstAsync(cancellationToken);
                GameNightOutbox.Enqueue(db, clock, DeliveryType.ResultDisputed, channel,
                    new ResultDisputedPayload(result.Id, table.Id, guildId, subject.Value, roles));
            }
        }
        else
        {
            SettleIfConfirmed(result, table, memberIds, now, Mode(configuration));
            if (statusBefore != ResultStatus.Confirmed && result.Status == ResultStatus.Confirmed)
            {
                Notice(db, clock, DeliveryType.ResultSettled, await ChannelOfAsync(db, table, cancellationToken), result, guildId, null);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok((await ToDtosAsync(db, guildId, [result], null, cancellationToken))[0]);
    }

    // ---- moderator ----

    private static async Task<IResult> Resolve(
        long guildId, Guid resultId, ResolveResultRequest request, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Action))
        {
            return Bad("Choose to confirm or void the result.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"""SELECT "Id" FROM "GameNightResults" WHERE "Id" = {resultId} FOR UPDATE""", cancellationToken);
        var result = await FindResultAsync(db, guildId, resultId, track: true, cancellationToken);
        if (result is null)
        {
            return Results.NotFound();
        }

        var table = (await TableEndpoints.FindTableAsync(db, guildId, result.TableId, track: true, cancellationToken))!;
        if (result.Status == ResultStatus.Voided)
        {
            return Results.Conflict(new ErrorResponse("This result was already withdrawn."));
        }

        var before = result.Status;
        if (request.Action == ResolveAction.Confirm)
        {
            if (result.Status != ResultStatus.Confirmed)
            {
                result.Status = ResultStatus.Confirmed;
                result.ConfirmedAt = clock.GetCurrentInstant();
                table.Status = TableStatus.Played;
            }
        }
        else
        {
            var wasConfirmed = result.Status == ResultStatus.Confirmed;
            result.Status = ResultStatus.Voided;
            result.ConfirmedAt = null;
            if (wasConfirmed && table.Status == TableStatus.Played)
            {
                table.Status = TableStatus.Open; // the table can report again
            }
        }

        if (result.Status != before)
        {
            Notice(db, clock, DeliveryType.ResultSettled, await ChannelOfAsync(db, table, cancellationToken), result, guildId, null);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok((await ToDtosAsync(db, guildId, [result], null, cancellationToken))[0]);
    }

    private static async Task<IResult> SetSummaryMessage(
        long guildId, Guid resultId, SetResultMessageRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var result = await FindResultAsync(db, guildId, resultId, track: true, cancellationToken);
        if (result is null)
        {
            return Results.NotFound();
        }

        result.SummaryMessageId = request.MessageId;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok((await ToDtosAsync(db, guildId, [result], null, cancellationToken))[0]);
    }

    // ---- helpers ----

    private static void Notice(
        CalCronyDbContext db, IClock clock, DeliveryType type, long channelId, GameNightResult result, long guildId, Duration? delay) =>
        GameNightOutbox.Enqueue(db, clock, type, channelId, new ResultNoticePayload(result.Id, guildId, result.TableId), delay);

    private static Task<long> ChannelOfAsync(CalCronyDbContext db, GameNightTable table, CancellationToken cancellationToken) =>
        db.Events.AsNoTracking().Where(e => e.Id == table.EventId).Select(e => e.ChannelId).FirstAsync(cancellationToken);

    /// <summary>How long a reported result waits before the bot reminds the people who have not answered; null for never
    /// (<c>GameNights:ResultReminderHours</c>, default 24, 0 turns it off).</summary>
    private static int? ReminderHours(IConfiguration configuration) =>
        Math.Clamp(configuration.GetValue("GameNights:ResultReminderHours", 24), 0, 168) is var hours && hours > 0 ? hours : null;

    /// <summary>Confirms the result (and marks the table played) when enough of the table has confirmed.</summary>
    private static void SettleIfConfirmed(
        GameNightResult result, GameNightTable table, IReadOnlyCollection<long> memberIds, Instant now, ConfirmMode mode)
    {
        var confirmed = result.Confirmations.Where(c => c.Decision == ConfirmationDecision.Confirm).Select(c => c.UserId).ToList();
        if (result.Status == ResultStatus.Pending && ResultRules.IsConfirmed(mode, memberIds, confirmed))
        {
            result.Status = ResultStatus.Confirmed;
            result.ConfirmedAt = now;
            table.Status = TableStatus.Played;
        }
    }

    private static ConfirmMode Mode(IConfiguration configuration) =>
        Enum.TryParse<ConfirmMode>(configuration[ConfirmModeKey], ignoreCase: true, out var mode) ? mode : ConfirmMode.All;

    private static IQueryable<GameNightResult> LoadResults(CalCronyDbContext db) =>
        db.GameNightResults.Include(r => r.Players).Include(r => r.Values).Include(r => r.Confirmations);

    private static async Task<GameNightResult?> FindResultAsync(
        CalCronyDbContext db, long guildId, Guid resultId, bool track, CancellationToken cancellationToken)
    {
        var query = LoadResults(db);
        if (!track)
        {
            query = query.AsNoTracking();
        }

        return await query
            .Where(r => r.Id == resultId)
            .Where(r => db.GameNightTables.Any(t => t.Id == r.TableId && db.Events.Any(e => e.Id == t.EventId && e.GuildId == guildId)))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<bool> CanSeeAsync(
        CalCronyDbContext db, long guildId, GameNightResult result, long userId, GameNightsTier tier, CancellationToken cancellationToken)
    {
        var table = await TableEndpoints.FindTableAsync(db, guildId, result.TableId, track: false, cancellationToken);
        return table is not null && await TableEndpoints.CanSeeTableAsync(db, table, userId, tier, cancellationToken);
    }

    private static async Task<IReadOnlyList<ResultDto>> ToDtosAsync(
        CalCronyDbContext db, long guildId, IReadOnlyList<GameNightResult> results, IReadOnlyDictionary<long, string>? overrides,
        CancellationToken cancellationToken)
    {
        var memberIds = results.SelectMany(r => r.Players).Where(p => p.UserId is not null).Select(p => p.UserId!.Value).Distinct().ToList();
        var names = await NameService.ResolveAsync(db, guildId, memberIds, overrides, cancellationToken);
        var guestIds = results.SelectMany(r => r.Players).Where(p => p.GuestId is not null).Select(p => p.GuestId!.Value).Distinct().ToList();
        var guests = await db.GameNightGuests.AsNoTracking().Where(g => guestIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.DisplayName, cancellationToken);
        var gameIds = results.Select(r => r.GameId).Distinct().ToList();
        var gameNames = await db.GameNightGames.AsNoTracking().Where(g => gameIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        var tableEvents = results.Select(r => r.TableId).Distinct().ToList();
        var eventOf = await db.GameNightTables.AsNoTracking().Where(t => tableEvents.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.EventId, cancellationToken);

        return [.. results.Select(r =>
        {
            var members = r.Players.Where(p => p.UserId is not null).Select(p => p.UserId!.Value).ToList();
            var confirmed = r.Confirmations.Where(c => c.Decision == ConfirmationDecision.Confirm).Select(c => c.UserId).ToHashSet();
            return new ResultDto(
                r.Id, r.TableId, eventOf.GetValueOrDefault(r.TableId), r.GameId, gameNames.GetValueOrDefault(r.GameId, "Game"), r.Status,
                r.ReporterUserId, r.SubmittedAt.ToDateTimeOffset(), r.ConfirmedAt?.ToDateTimeOffset(),
                [.. r.Players.OrderBy(p => p.Placement ?? int.MaxValue).Select(p => new ResultPlayerDto(
                    p.UserId, p.GuestId,
                    p.UserId is { } u ? names[u].DisplayName : guests.GetValueOrDefault(p.GuestId!.Value, DisplayNames.FormerGuest),
                    p.Placement, p.Score,
                    p.ValuesJson is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(p.ValuesJson)!))],
                r.Values.ToDictionary(v => v.Key, v => v.Value),
                [.. r.Confirmations.OrderBy(c => c.At).Select(c => new ResultConfirmationDto(c.UserId, c.Decision))],
                r.Status == ResultStatus.Pending ? [.. members.Where(m => !confirmed.Contains(m))] : [],
                [.. members.Where(m => names[m].Unnamed)], r.SummaryMessageId);
        })];
    }

    private static IResult Bad(string message) => Results.BadRequest(new ErrorResponse(message));
}
