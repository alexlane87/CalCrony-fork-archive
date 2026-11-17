using System.Net.Http.Json;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;
using CalCrony.Web.Api;
using Unit = CalCrony.Web.Api.CalCronyWebApiClient.Unit;

namespace CalCrony.Web.GameNights;

/// <summary>Typed client for the Game Nights API routes (a sibling of <see cref="CalCronyWebApiClient"/>,
/// so the core client stays untouched). Results use the web's core <see cref="ApiResult{T}"/>.
/// The caller's tier is cached briefly per server: every guild page asks it (to decide whether to
/// show the Venues tab at all), and a 404 here simply means Game Nights is not switched on.</summary>
/// <param name="http">The configured HTTP client.</param>
public sealed class GameNightsWebApiClient(HttpClient http)
{
    private static readonly TimeSpan TierCacheFor = TimeSpan.FromSeconds(30);

    private readonly Dictionary<long, (DateTimeOffset At, ApiResult<MyTierResponse> Result)> tiers = [];

    /// <summary>The signed-in user's tier in a server. A not-found result means the feature is off.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The tier, or the failure.</returns>
    public async Task<ApiResult<MyTierResponse>> GetMyTierAsync(long guildId, CancellationToken ct = default)
    {
        if (tiers.TryGetValue(guildId, out var hit) && DateTimeOffset.UtcNow - hit.At < TierCacheFor)
        {
            return hit.Result;
        }

        var result = await SendAsync<MyTierResponse>(http.GetAsync($"/guilds/{guildId}/game-nights/me", ct), ct);
        tiers[guildId] = (DateTimeOffset.UtcNow, result);
        return result;
    }

    /// <summary>A server's venues.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="includeRetired">Include retired venues.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The venues, or the failure.</returns>
    public Task<ApiResult<List<VenueDto>>> ListVenuesAsync(long guildId, bool includeRetired = false, CancellationToken ct = default) =>
        SendAsync<List<VenueDto>>(http.GetAsync($"/guilds/{guildId}/game-nights/venues?includeRetired={includeRetired}", ct), ct);

    /// <summary>Creates a venue.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="request">The new venue.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The created venue, or the failure.</returns>
    public Task<ApiResult<VenueDto>> CreateVenueAsync(long guildId, CreateVenueRequest request, CancellationToken ct = default) =>
        SendAsync<VenueDto>(http.PostAsJsonAsync($"/guilds/{guildId}/game-nights/venues", request, ct), ct);

    /// <summary>Edits a venue.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="venueId">The venue.</param>
    /// <param name="request">The changes.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The updated venue, or the failure.</returns>
    public Task<ApiResult<VenueDto>> UpdateVenueAsync(long guildId, Guid venueId, UpdateVenueRequest request, CancellationToken ct = default) =>
        SendAsync<VenueDto>(http.PatchAsJsonAsync($"/guilds/{guildId}/game-nights/venues/{venueId}", request, ct), ct);

    /// <summary>Deletes a venue nothing has used.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="venueId">The venue.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>Success, or the failure.</returns>
    public Task<ApiResult<Unit>> DeleteVenueAsync(long guildId, Guid venueId, CancellationToken ct = default) =>
        SendAsync<Unit>(http.DeleteAsync($"/guilds/{guildId}/game-nights/venues/{venueId}", ct), ct);

    /// <summary>Adds an area to a venue.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="venueId">The venue.</param>
    /// <param name="request">The new area.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The created area, or the failure.</returns>
    public Task<ApiResult<VenueAreaDto>> CreateAreaAsync(long guildId, Guid venueId, CreateVenueAreaRequest request, CancellationToken ct = default) =>
        SendAsync<VenueAreaDto>(http.PostAsJsonAsync($"/guilds/{guildId}/game-nights/venues/{venueId}/areas", request, ct), ct);

    /// <summary>Edits an area.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="venueId">The venue.</param>
    /// <param name="areaId">The area.</param>
    /// <param name="request">The changes.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The updated area, or the failure.</returns>
    public Task<ApiResult<VenueAreaDto>> UpdateAreaAsync(
        long guildId, Guid venueId, Guid areaId, UpdateVenueAreaRequest request, CancellationToken ct = default) =>
        SendAsync<VenueAreaDto>(http.PatchAsJsonAsync($"/guilds/{guildId}/game-nights/venues/{venueId}/areas/{areaId}", request, ct), ct);

    /// <summary>Deletes an area nothing has used.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="venueId">The venue.</param>
    /// <param name="areaId">The area.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>Success, or the failure.</returns>
    public Task<ApiResult<Unit>> DeleteAreaAsync(long guildId, Guid venueId, Guid areaId, CancellationToken ct = default) =>
        SendAsync<Unit>(http.DeleteAsync($"/guilds/{guildId}/game-nights/venues/{venueId}/areas/{areaId}", ct), ct);

    /// <summary>An event's venue settings and effective limit.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="eventId">The event.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The settings, or the failure.</returns>
    public Task<ApiResult<EventVenueDto>> GetEventVenueAsync(long guildId, Guid eventId, CancellationToken ct = default) =>
        SendAsync<EventVenueDto>(http.GetAsync($"/guilds/{guildId}/game-nights/events/{eventId}/venue", ct), ct);

    /// <summary>Sets an event's venue, override and areas.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="eventId">The event.</param>
    /// <param name="request">The new settings.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The resulting settings, or the failure (409 when the limit would unseat people).</returns>
    public Task<ApiResult<EventVenueDto>> SetEventVenueAsync(long guildId, Guid eventId, SetEventVenueRequest request, CancellationToken ct = default) =>
        SendAsync<EventVenueDto>(http.PutAsJsonAsync($"/guilds/{guildId}/game-nights/events/{eventId}/venue", request, ct), ct);

    /// <summary>What a night's game answers add up to.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="eventId">The night.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The summary, or the failure.</returns>
    public Task<ApiResult<NightSignalsSummary>> GetSignalSummaryAsync(long guildId, Guid eventId, CancellationToken ct = default) =>
        SendAsync<NightSignalsSummary>(http.GetAsync($"/guilds/{guildId}/game-nights/events/{eventId}/signals", ct), ct);

    /// <summary>Who can teach what on a night.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="eventId">The night.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The roster, or the failure.</returns>
    public Task<ApiResult<List<TeacherRosterEntryDto>>> GetRosterAsync(long guildId, Guid eventId, CancellationToken ct = default) =>
        SendAsync<List<TeacherRosterEntryDto>>(http.GetAsync($"/guilds/{guildId}/game-nights/events/{eventId}/signals/roster", ct), ct);

    /// <summary>Everyone's answers for a night (organisers).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="eventId">The night.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The answers, or the failure.</returns>
    public Task<ApiResult<List<PersonSignalDto>>> GetPeopleAsync(long guildId, Guid eventId, CancellationToken ct = default) =>
        SendAsync<List<PersonSignalDto>>(http.GetAsync($"/guilds/{guildId}/game-nights/events/{eventId}/signals/people", ct), ct);

    /// <summary>The signed-in member's answers for a night.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="eventId">The night.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The answers, or the failure.</returns>
    public Task<ApiResult<MySignalsDto>> GetMySignalsAsync(long guildId, Guid eventId, CancellationToken ct = default) =>
        SendAsync<MySignalsDto>(http.GetAsync($"/guilds/{guildId}/game-nights/events/{eventId}/signals/mine", ct), ct);

    /// <summary>Replaces the signed-in member's answers for a night.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="eventId">The night.</param>
    /// <param name="signals">The full set of answers.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The saved answers, or the failure.</returns>
    public Task<ApiResult<MySignalsDto>> SetMySignalsAsync(long guildId, Guid eventId, IReadOnlyList<SignalDto> signals, CancellationToken ct = default) =>
        SendAsync<MySignalsDto>(http.PutAsJsonAsync($"/guilds/{guildId}/game-nights/events/{eventId}/signals/mine", new SetMySignalsRequest(signals), ct), ct);

    /// <summary>A server's games (active ones unless retired ones are asked for).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <param name="includeRetired">Include retired games.</param>
    /// <returns>The games, or the failure.</returns>
    public Task<ApiResult<List<GameDto>>> ListGamesAsync(long guildId, CancellationToken ct = default, bool includeRetired = false) =>
        SendAsync<List<GameDto>>(http.GetAsync($"/guilds/{guildId}/game-nights/games?includeRetired={includeRetired}", ct), ct);

    /// <summary>Adds a game to the library.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="spec">The game's definition.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The created game, or the failure.</returns>
    public Task<ApiResult<GameDto>> CreateGameAsync(long guildId, GameSpec spec, CancellationToken ct = default) =>
        SendAsync<GameDto>(http.PostAsJsonAsync($"/guilds/{guildId}/game-nights/games", spec, ct), ct);

    /// <summary>Replaces a game's definition (used to attach or change its role).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="gameId">The game.</param>
    /// <param name="spec">The new definition.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The game, or the failure.</returns>
    public Task<ApiResult<GameDto>> UpdateGameAsync(long guildId, Guid gameId, GameSpec spec, CancellationToken ct = default) =>
        SendAsync<GameDto>(http.PutAsJsonAsync($"/guilds/{guildId}/game-nights/games/{gameId}", spec, ct), ct);

    /// <summary>Retires or reinstates a game.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="gameId">The game.</param>
    /// <param name="active">True to reinstate, false to retire.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The game, or the failure.</returns>
    public Task<ApiResult<GameDto>> SetGameActiveAsync(long guildId, Guid gameId, bool active, CancellationToken ct = default) =>
        SendAsync<GameDto>(http.PostAsync($"/guilds/{guildId}/game-nights/games/{gameId}/{(active ? "reinstate" : "retire")}", null, ct), ct);

    /// <summary>Asks the bot to create the game's Discord role.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="gameId">The game.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The game (role pending), or the failure.</returns>
    public Task<ApiResult<GameDto>> RequestRoleAsync(long guildId, Guid gameId, CancellationToken ct = default) =>
        SendAsync<GameDto>(http.PostAsync($"/guilds/{guildId}/game-nights/games/{gameId}/role/create", null, ct), ct);

    /// <summary>Follows or unfollows a game (the bot gives or takes its role).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="gameId">The game.</param>
    /// <param name="follow">True to follow.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>Success once the request is queued, or the failure.</returns>
    public Task<ApiResult<Unit>> FollowGameAsync(long guildId, Guid gameId, bool follow, CancellationToken ct = default) =>
        SendAsync<Unit>(http.PostAsJsonAsync($"/guilds/{guildId}/game-nights/games/{gameId}/follow", new FollowGameRequest(follow), ct), ct);

    /// <summary>Which games the signed-in user follows, as far as the role snapshot can say.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The answer, or the failure.</returns>
    public Task<ApiResult<FollowingResponse>> GetFollowingAsync(long guildId, CancellationToken ct = default) =>
        SendAsync<FollowingResponse>(http.GetAsync($"/guilds/{guildId}/game-nights/games/following", ct), ct);

    /// <summary>Applies the standard games file (Root, Arcs) to the server.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>What was added, or the failure.</returns>
    public Task<ApiResult<SeedGamesResponse>> SeedGamesAsync(long guildId, CancellationToken ct = default) =>
        SendAsync<SeedGamesResponse>(http.PostAsync($"/guilds/{guildId}/game-nights/games/seed", null, ct), ct);

    /// <summary>The roles an admin has marked as game types.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The types, or the failure.</returns>
    public Task<ApiResult<List<GameTypeDto>>> ListGameTypesAsync(long guildId, CancellationToken ct = default) =>
        SendAsync<List<GameTypeDto>>(http.GetAsync($"/guilds/{guildId}/game-nights/game-types", ct), ct);

    /// <summary>Replaces the game-type list.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="types">The full list.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The saved list, or the failure.</returns>
    public Task<ApiResult<List<GameTypeDto>>> PutGameTypesAsync(long guildId, IReadOnlyList<GameTypeDto> types, CancellationToken ct = default) =>
        SendAsync<List<GameTypeDto>>(http.PutAsJsonAsync($"/guilds/{guildId}/game-nights/game-types", new PutGameTypesRequest(types), ct), ct);

    /// <summary>A night's tables.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="eventId">The event.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The tables, or the failure.</returns>
    public Task<ApiResult<EventTablesDto>> ListTablesAsync(long guildId, Guid eventId, CancellationToken ct = default) =>
        SendAsync<EventTablesDto>(http.GetAsync($"/guilds/{guildId}/game-nights/events/{eventId}/tables", ct), ct);

    /// <summary>One table.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="tableId">The table.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The table, or the failure.</returns>
    public Task<ApiResult<TableDto>> GetTableAsync(long guildId, Guid tableId, CancellationToken ct = default) =>
        SendAsync<TableDto>(http.GetAsync($"/guilds/{guildId}/game-nights/tables/{tableId}", ct), ct);

    /// <summary>Opens a table.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="eventId">The event.</param>
    /// <param name="request">The new table.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The table, or the failure.</returns>
    public Task<ApiResult<TableDto>> CreateTableAsync(long guildId, Guid eventId, CreateTableRequest request, CancellationToken ct = default) =>
        SendAsync<TableDto>(http.PostAsJsonAsync($"/guilds/{guildId}/game-nights/events/{eventId}/tables", request, ct), ct);

    /// <summary>Changes a table's size or host.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="tableId">The table.</param>
    /// <param name="request">The changes.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The table, or the failure.</returns>
    public Task<ApiResult<TableDto>> UpdateTableAsync(long guildId, Guid tableId, UpdateTableRequest request, CancellationToken ct = default) =>
        SendAsync<TableDto>(http.PatchAsJsonAsync($"/guilds/{guildId}/game-nights/tables/{tableId}", request, ct), ct);

    /// <summary>Cancels a table.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="tableId">The table.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The table, or the failure.</returns>
    public Task<ApiResult<TableDto>> CancelTableAsync(long guildId, Guid tableId, CancellationToken ct = default) =>
        SendAsync<TableDto>(http.PostAsync($"/guilds/{guildId}/game-nights/tables/{tableId}/cancel", null, ct), ct);

    /// <summary>Sits a person down at a table (members: themselves).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="tableId">The table.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The table, or the failure.</returns>
    public Task<ApiResult<TableDto>> SeatAsync(long guildId, Guid tableId, long userId, CancellationToken ct = default) =>
        SendAsync<TableDto>(http.PostAsJsonAsync($"/guilds/{guildId}/game-nights/tables/{tableId}/seats", new SeatRequest(userId), ct), ct);

    /// <summary>Stands a person up from a table (members: themselves).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="tableId">The table.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The table, or the failure.</returns>
    public Task<ApiResult<TableDto>> UnseatAsync(long guildId, Guid tableId, long userId, CancellationToken ct = default) =>
        SendAsync<TableDto>(http.DeleteAsync($"/guilds/{guildId}/game-nights/tables/{tableId}/seats/{userId}", ct), ct);

    /// <summary>Sets the night's default table size.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="eventId">The event.</param>
    /// <param name="size">The size, or null to clear it.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The saved default, or the failure.</returns>
    public Task<ApiResult<SetTableDefaultsRequest>> SetTableDefaultAsync(long guildId, Guid eventId, int? size, CancellationToken ct = default) =>
        SendAsync<SetTableDefaultsRequest>(
            http.PutAsJsonAsync($"/guilds/{guildId}/game-nights/events/{eventId}/table-defaults", new SetTableDefaultsRequest(size), ct), ct);

    // ---- results ----

    /// <summary>The server's most recent results the caller may see, newest first.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="gameId">Only this game, or null for all.</param>
    /// <param name="userId">Only results this member played in, or null for all.</param>
    /// <param name="status">Only this status, or null for all.</param>
    /// <param name="take">How many (up to 50).</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The results, or the failure.</returns>
    public Task<ApiResult<List<ResultDto>>> ListResultsAsync(
        long guildId, Guid? gameId = null, long? userId = null, ResultStatus? status = null, int take = 25, CancellationToken ct = default)
    {
        var query = $"take={take}";
        query += gameId is { } g ? $"&gameId={g}" : "";
        query += userId is { } u ? $"&userId={u}" : "";
        query += status is { } s ? $"&status={(int)s}" : "";
        return SendAsync<List<ResultDto>>(http.GetAsync($"/guilds/{guildId}/game-nights/results?{query}", ct), ct);
    }

    /// <summary>The results reported for one table.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="tableId">The table.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The results, or the failure.</returns>
    public Task<ApiResult<List<ResultDto>>> ListTableResultsAsync(long guildId, Guid tableId, CancellationToken ct = default) =>
        SendAsync<List<ResultDto>>(http.GetAsync($"/guilds/{guildId}/game-nights/tables/{tableId}/results", ct), ct);

    /// <summary>Reports a result for a table. The board photo is never part of it: it stays in the table's Discord thread.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="tableId">The table.</param>
    /// <param name="submission">Who played and the answers.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The stored result, or the failure.</returns>
    public Task<ApiResult<ResultDto>> SubmitResultAsync(long guildId, Guid tableId, ResultSubmission submission, CancellationToken ct = default) =>
        SendAsync<ResultDto>(http.PostAsJsonAsync($"/guilds/{guildId}/game-nights/tables/{tableId}/results", new SubmitResultRequest(submission), ct), ct);

    /// <summary>Confirms a result as the signed-in player.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="resultId">The result.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The result as it now stands, or the failure.</returns>
    public Task<ApiResult<ResultDto>> ConfirmResultAsync(long guildId, Guid resultId, CancellationToken ct = default) =>
        SendAsync<ResultDto>(http.PostAsJsonAsync($"/guilds/{guildId}/game-nights/results/{resultId}/confirm", new AnswerResultRequest(), ct), ct);

    /// <summary>Rejects a result as the signed-in player.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="resultId">The result.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The result as it now stands, or the failure.</returns>
    public Task<ApiResult<ResultDto>> RejectResultAsync(long guildId, Guid resultId, CancellationToken ct = default) =>
        SendAsync<ResultDto>(http.PostAsJsonAsync($"/guilds/{guildId}/game-nights/results/{resultId}/reject", new AnswerResultRequest(), ct), ct);

    /// <summary>A moderator confirms or voids a result.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="resultId">The result.</param>
    /// <param name="action">Confirm or void.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The result as it now stands, or the failure.</returns>
    public Task<ApiResult<ResultDto>> ResolveResultAsync(long guildId, Guid resultId, ResolveAction action, CancellationToken ct = default) =>
        SendAsync<ResultDto>(http.PostAsJsonAsync($"/guilds/{guildId}/game-nights/results/{resultId}/resolve", new ResolveResultRequest(action), ct), ct);

    // ---- leagues ----

    /// <summary>A game's league table.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="gameId">The game.</param>
    /// <param name="seasonId">A season to show, or null for the running season (else all time).</param>
    /// <param name="allTime">True to force the all-time table.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The standings, or the failure.</returns>
    public Task<ApiResult<LeagueStandingsDto>> GetStandingsAsync(
        long guildId, Guid gameId, Guid? seasonId = null, bool allTime = false, CancellationToken ct = default)
    {
        var query = allTime ? "?allTime=true" : seasonId is { } s ? $"?seasonId={s}" : "";
        return SendAsync<LeagueStandingsDto>(http.GetAsync($"/guilds/{guildId}/game-nights/games/{gameId}/league{query}", ct), ct);
    }

    /// <summary>One player's rating history in a game, oldest first.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="gameId">The game.</param>
    /// <param name="userId">The member.</param>
    /// <param name="guestId">A guest, instead of a member.</param>
    /// <param name="seasonId">A season, or null for the table shown by default.</param>
    /// <param name="allTime">True to force all time.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The history, or the failure.</returns>
    public Task<ApiResult<List<RatingHistoryEntryDto>>> GetRatingHistoryAsync(
        long guildId, Guid gameId, long? userId = null, Guid? guestId = null, Guid? seasonId = null, bool allTime = false, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (userId is { } u) { query.Add($"userId={u}"); }
        if (guestId is { } gu) { query.Add($"guestId={gu}"); }
        if (allTime) { query.Add("allTime=true"); }
        else if (seasonId is { } s) { query.Add($"seasonId={s}"); }
        var suffix = query.Count == 0 ? "" : "?" + string.Join('&', query);
        return SendAsync<List<RatingHistoryEntryDto>>(http.GetAsync($"/guilds/{guildId}/game-nights/games/{gameId}/league/history{suffix}", ct), ct);
    }

    /// <summary>A member's standing in each game's league.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="userId">The member, or null for the signed-in user.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>Their ratings, or the failure.</returns>
    public Task<ApiResult<List<MyRatingDto>>> GetRatingsAsync(long guildId, long? userId = null, CancellationToken ct = default) =>
        SendAsync<List<MyRatingDto>>(http.GetAsync($"/guilds/{guildId}/game-nights/leagues/me{(userId is { } u ? $"?userId={u}" : "")}", ct), ct);

    /// <summary>Starts a season (organisers). Starting one ends the running season.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="gameId">The game.</param>
    /// <param name="request">The season.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The season, or the failure.</returns>
    public Task<ApiResult<SeasonDto>> CreateSeasonAsync(long guildId, Guid gameId, CreateSeasonRequest request, CancellationToken ct = default) =>
        SendAsync<SeasonDto>(http.PostAsJsonAsync($"/guilds/{guildId}/game-nights/games/{gameId}/league/seasons", request, ct), ct);

    /// <summary>Ends a season now (organisers).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="gameId">The game.</param>
    /// <param name="seasonId">The season.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The season, or the failure.</returns>
    public Task<ApiResult<SeasonDto>> EndSeasonAsync(long guildId, Guid gameId, Guid seasonId, CancellationToken ct = default) =>
        SendAsync<SeasonDto>(http.PostAsync($"/guilds/{guildId}/game-nights/games/{gameId}/league/seasons/{seasonId}/end", null, ct), ct);

    /// <summary>Rebuilds a game's ratings from its confirmed results (moderators).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="gameId">The game.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>Success, or the failure.</returns>
    public Task<ApiResult<Unit>> RecomputeLeagueAsync(long guildId, Guid gameId, CancellationToken ct = default) =>
        SendAsync<Unit>(http.PostAsync($"/guilds/{guildId}/game-nights/games/{gameId}/league/recompute", null, ct), ct);

    // ---- names and guests ----

    private readonly Dictionary<(long Guild, long User), string> names = [];

    /// <summary>"First L" names for members, as the API's name service decides them (never a stored real name, nickname or
    /// username). Looked up in batches and remembered for this visit; anyone it could not name shows as "Unnamed player".</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="userIds">The members.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>A name for every id asked about.</returns>
    public async Task<IReadOnlyDictionary<long, string>> GetNamesAsync(long guildId, IEnumerable<long> userIds, CancellationToken ct = default)
    {
        var wanted = userIds.Where(id => id > 0).Distinct().ToList();
        var missing = wanted.Where(id => !names.ContainsKey((guildId, id))).ToList();
        for (var i = 0; i < missing.Count; i += 100)
        {
            var batch = string.Join(',', missing.Skip(i).Take(100));
            var found = await SendAsync<Dictionary<string, string>>(http.GetAsync($"/guilds/{guildId}/game-nights/names?ids={batch}", ct), ct);
            foreach (var (key, value) in found.Value ?? [])
            {
                if (long.TryParse(key, out var id))
                {
                    names[(guildId, id)] = value;
                }
            }
        }

        return wanted.ToDictionary(id => id, id => names.GetValueOrDefault((guildId, id), DisplayNames.UnnamedPlayer));
    }

    /// <summary>The server's guests (organisers).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The guests, or the failure.</returns>
    public Task<ApiResult<List<GuestDto>>> ListGuestsAsync(long guildId, CancellationToken ct = default) =>
        SendAsync<List<GuestDto>>(http.GetAsync($"/guilds/{guildId}/game-nights/guests", ct), ct);

    /// <summary>Adds a guest (organisers).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="displayName">The guest's name as "First L".</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The guest, or the failure.</returns>
    public Task<ApiResult<GuestDto>> CreateGuestAsync(long guildId, string displayName, CancellationToken ct = default) =>
        SendAsync<GuestDto>(http.PostAsJsonAsync($"/guilds/{guildId}/game-nights/guests", new CreateGuestRequest(displayName), ct), ct);

    /// <summary>Erases a guest's name on request (moderators).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="guestId">The guest.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>Success, or the failure.</returns>
    public Task<ApiResult<Unit>> EraseGuestAsync(long guildId, Guid guestId, CancellationToken ct = default) =>
        SendAsync<Unit>(http.DeleteAsync($"/guilds/{guildId}/game-nights/guests/{guestId}", ct), ct);

    private static async Task<ApiResult<T>> SendAsync<T>(Task<HttpResponseMessage> sending, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await sending;
        }
        catch (HttpRequestException ex)
        {
            return new ApiResult<T>(default, $"API unreachable: {ex.Message}");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                if (typeof(T) == typeof(Unit) || response.Content.Headers.ContentLength == 0)
                {
                    return new ApiResult<T>(default, null);
                }

                return new ApiResult<T>(await response.Content.ReadFromJsonAsync<T>(ct), null);
            }

            ErrorResponse? body = null;
            try
            {
                body = await response.Content.ReadFromJsonAsync<ErrorResponse>(ct);
            }
            catch
            {
                // Non-JSON error body; fall through to the status-code message.
            }

            return new ApiResult<T>(default, body?.Error ?? $"API error {(int)response.StatusCode}.", response.StatusCode, body?.Code);
        }
    }
}
