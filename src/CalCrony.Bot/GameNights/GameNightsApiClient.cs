using System.Net.Http.Json;
using CalCrony.Bot.Api;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;

namespace CalCrony.Bot.GameNights;

/// <summary>Typed client for the Game Nights API routes (a sibling of <see cref="CalCronyApiClient"/>,
/// so the core client file stays untouched). Same base address and API key; results use the core
/// <see cref="ApiResult{T}"/> shape.</summary>
/// <param name="http">The configured HTTP client.</param>
public sealed class GameNightsApiClient(HttpClient http)
{
    /// <summary>A guild's role-to-tier mapping.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The mapping, or a display-ready error.</returns>
    public Task<ApiResult<TierRolesResponse>> GetTierRolesAsync(long guildId, CancellationToken ct = default) =>
        SendAsync<TierRolesResponse>(http.GetAsync($"/guilds/{guildId}/game-nights/tier-roles", ct), ct);

    /// <summary>An event's venue settings.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="eventId">The event id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The settings, or a display-ready error.</returns>
    public Task<ApiResult<EventVenueDto>> GetEventVenueAsync(long guildId, Guid eventId, CancellationToken ct = default) =>
        SendAsync<EventVenueDto>(http.GetAsync($"/guilds/{guildId}/game-nights/events/{eventId}/venue", ct), ct);

    /// <summary>Switches one of an event's venue areas on or off.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="eventId">The event id.</param>
    /// <param name="areaId">The area id.</param>
    /// <param name="on">True to switch the area on.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The updated settings, or a display-ready error (for example the new limit would be
    /// below the seats already taken).</returns>
    public Task<ApiResult<EventVenueDto>> SetEventAreaAsync(
        long guildId, Guid eventId, Guid areaId, bool on, CancellationToken ct = default) =>
        SendAsync<EventVenueDto>(
            http.PutAsJsonAsync($"/guilds/{guildId}/game-nights/events/{eventId}/venue/areas/{areaId}", new SetEventAreaRequest(on), ct), ct);

    /// <summary>A game's league table: the running season's, or the all-time one.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="gameId">The game.</param>
    /// <param name="allTime">True for the all-time table.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The table, or a display-ready error.</returns>
    public Task<ApiResult<LeagueStandingsDto>> GetStandingsAsync(long guildId, Guid gameId, bool allTime = false, CancellationToken ct = default) =>
        SendAsync<LeagueStandingsDto>(http.GetAsync($"/guilds/{guildId}/game-nights/games/{gameId}/league?allTime={allTime}", ct), ct);

    /// <summary>A member's ratings across the league tables.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="userId">The member.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>Their ratings, or a display-ready error.</returns>
    public Task<ApiResult<List<MyRatingDto>>> GetMyRatingsAsync(long guildId, long userId, CancellationToken ct = default) =>
        SendAsync<List<MyRatingDto>>(http.GetAsync($"/guilds/{guildId}/game-nights/leagues/me?userId={userId}", ct), ct);

    /// <summary>Reports a result for a table (the bot names the reporter).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="tableId">The table.</param>
    /// <param name="request">The report.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The stored result, or a display-ready error.</returns>
    public Task<ApiResult<ResultDto>> SubmitResultAsync(long guildId, Guid tableId, SubmitResultRequest request, CancellationToken ct = default) =>
        SendAsync<ResultDto>(http.PostAsJsonAsync($"/guilds/{guildId}/game-nights/tables/{tableId}/results", request, ct), ct);

    /// <summary>A reported result.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="resultId">The result.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The result, or a display-ready error (NotFound when it is gone).</returns>
    public Task<ApiResult<ResultDto>> GetResultAsync(long guildId, Guid resultId, CancellationToken ct = default) =>
        SendAsync<ResultDto>(http.GetAsync($"/guilds/{guildId}/game-nights/results/{resultId}", ct), ct);

    /// <summary>A player confirms or rejects a result.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="resultId">The result.</param>
    /// <param name="confirm">True to confirm, false to reject.</param>
    /// <param name="userId">The player.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The result, or a display-ready error.</returns>
    public Task<ApiResult<ResultDto>> AnswerResultAsync(long guildId, Guid resultId, bool confirm, long userId, CancellationToken ct = default) =>
        SendAsync<ResultDto>(
            http.PostAsJsonAsync($"/guilds/{guildId}/game-nights/results/{resultId}/{(confirm ? "confirm" : "reject")}", new AnswerResultRequest(userId), ct), ct);

    /// <summary>Records the message the bot posted for a result.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="resultId">The result.</param>
    /// <param name="messageId">The message id, or null to forget it.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The result, or a display-ready error.</returns>
    public Task<ApiResult<ResultDto>> SetResultMessageAsync(long guildId, Guid resultId, long? messageId, CancellationToken ct = default) =>
        SendAsync<ResultDto>(
            http.PutAsJsonAsync($"/guilds/{guildId}/game-nights/results/{resultId}/summary-message", new SetResultMessageRequest(messageId), ct), ct);

    /// <summary>Replaces a game's definition.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="gameId">The game.</param>
    /// <param name="spec">The new definition.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The game, or a display-ready error.</returns>
    public Task<ApiResult<GameDto>> UpdateGameAsync(long guildId, Guid gameId, GameSpec spec, CancellationToken ct = default) =>
        SendAsync<GameDto>(http.PutAsJsonAsync($"/guilds/{guildId}/game-nights/games/{gameId}", spec, ct), ct);

    /// <summary>The members the API wants names for.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The members, or a display-ready error.</returns>
    public Task<ApiResult<WantedNamesResponse>> GetWantedNamesAsync(long guildId, CancellationToken ct = default) =>
        SendAsync<WantedNamesResponse>(http.GetAsync($"/guilds/{guildId}/game-nights/member-names/wanted", ct), ct);

    /// <summary>Pushes "First L" names (only the derived form).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="members">The names.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>Success, or a display-ready error.</returns>
    public async Task<ApiResult<bool>> PutMemberNamesAsync(long guildId, IReadOnlyList<MemberNameDto> members, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PutAsJsonAsync($"/guilds/{guildId}/game-nights/member-names", new PutMemberNamesRequest(members), ct);
            return response.IsSuccessStatusCode ? new ApiResult<bool>(true, null) : new ApiResult<bool>(false, $"API error {(int)response.StatusCode}.");
        }
        catch (HttpRequestException ex)
        {
            return new ApiResult<bool>(false, $"API unreachable: {ex.Message}");
        }
    }

    /// <summary>A member's answers for a night.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="eventId">The night.</param>
    /// <param name="userId">The member.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The answers, or a display-ready error.</returns>
    public Task<ApiResult<MySignalsDto>> GetMySignalsAsync(long guildId, Guid eventId, long userId, CancellationToken ct = default) =>
        SendAsync<MySignalsDto>(http.GetAsync($"/guilds/{guildId}/game-nights/events/{eventId}/signals/mine?userId={userId}", ct), ct);

    /// <summary>Records (or clears) one member's answer about one game.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="eventId">The night.</param>
    /// <param name="gameId">The game.</param>
    /// <param name="kind">The answer, or null to clear it.</param>
    /// <param name="userId">The member.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The member's answers, or a display-ready error.</returns>
    public Task<ApiResult<MySignalsDto>> SetSignalAsync(
        long guildId, Guid eventId, Guid gameId, SignalKind? kind, long userId, CancellationToken ct = default) =>
        SendAsync<MySignalsDto>(
            http.PutAsJsonAsync($"/guilds/{guildId}/game-nights/events/{eventId}/signals/{gameId}", new SetSignalRequest(kind, userId), ct), ct);

    /// <summary>A server's games (active ones unless retired ones are asked for).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <param name="includeRetired">Include retired games.</param>
    /// <returns>The games, or a display-ready error.</returns>
    public Task<ApiResult<List<GameDto>>> ListGamesAsync(long guildId, CancellationToken ct = default, bool includeRetired = false) =>
        SendAsync<List<GameDto>>(http.GetAsync($"/guilds/{guildId}/game-nights/games?includeRetired={includeRetired}", ct), ct);

    /// <summary>One game.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="gameId">The game.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The game, or a display-ready error (NotFound when it is gone).</returns>
    public Task<ApiResult<GameDto>> GetGameAsync(long guildId, Guid gameId, CancellationToken ct = default) =>
        SendAsync<GameDto>(http.GetAsync($"/guilds/{guildId}/game-nights/games/{gameId}", ct), ct);

    /// <summary>Adds a game to the library.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="spec">The game's definition.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The created game, or a display-ready error.</returns>
    public Task<ApiResult<GameDto>> CreateGameAsync(long guildId, GameSpec spec, CancellationToken ct = default) =>
        SendAsync<GameDto>(http.PostAsJsonAsync($"/guilds/{guildId}/game-nights/games", spec, ct), ct);

    /// <summary>Records the outcome of making or granting a game's role.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="gameId">The game.</param>
    /// <param name="request">The outcome.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The game, or a display-ready error.</returns>
    public Task<ApiResult<GameDto>> SetGameRoleStatusAsync(long guildId, Guid gameId, SetGameRoleStatusRequest request, CancellationToken ct = default) =>
        SendAsync<GameDto>(http.PutAsJsonAsync($"/guilds/{guildId}/game-nights/games/{gameId}/role-status", request, ct), ct);

    /// <summary>The roles an admin has marked as game types.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The types, or a display-ready error.</returns>
    public Task<ApiResult<List<GameTypeDto>>> ListGameTypesAsync(long guildId, CancellationToken ct = default) =>
        SendAsync<List<GameTypeDto>>(http.GetAsync($"/guilds/{guildId}/game-nights/game-types", ct), ct);

    /// <summary>Replaces the game-type list.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="types">The full list.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The saved list, or a display-ready error.</returns>
    public Task<ApiResult<List<GameTypeDto>>> PutGameTypesAsync(long guildId, IReadOnlyList<GameTypeDto> types, CancellationToken ct = default) =>
        SendAsync<List<GameTypeDto>>(http.PutAsJsonAsync($"/guilds/{guildId}/game-nights/game-types", new PutGameTypesRequest(types), ct), ct);

    /// <summary>A night's tables.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="eventId">The event.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The tables, or a display-ready error.</returns>
    public Task<ApiResult<EventTablesDto>> ListTablesAsync(long guildId, Guid eventId, CancellationToken ct = default) =>
        SendAsync<EventTablesDto>(http.GetAsync($"/guilds/{guildId}/game-nights/events/{eventId}/tables", ct), ct);

    /// <summary>One table.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="tableId">The table.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The table, or a display-ready error (NotFound when it is gone).</returns>
    public Task<ApiResult<TableDto>> GetTableAsync(long guildId, Guid tableId, CancellationToken ct = default) =>
        SendAsync<TableDto>(http.GetAsync($"/guilds/{guildId}/game-nights/tables/{tableId}", ct), ct);

    /// <summary>Opens a table at a night.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="eventId">The event.</param>
    /// <param name="request">The new table.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The table, or a display-ready error.</returns>
    public Task<ApiResult<TableDto>> CreateTableAsync(long guildId, Guid eventId, CreateTableRequest request, CancellationToken ct = default) =>
        SendAsync<TableDto>(http.PostAsJsonAsync($"/guilds/{guildId}/game-nights/events/{eventId}/tables", request, ct), ct);

    /// <summary>Sits someone down at a table.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="tableId">The table.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The table, or a display-ready error (full, closed, already seated).</returns>
    public Task<ApiResult<TableDto>> SeatAsync(long guildId, Guid tableId, long userId, CancellationToken ct = default) =>
        SendAsync<TableDto>(http.PostAsJsonAsync($"/guilds/{guildId}/game-nights/tables/{tableId}/seats", new SeatRequest(userId), ct), ct);

    /// <summary>Stands someone up from a table.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="tableId">The table.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The table, or a display-ready error.</returns>
    public Task<ApiResult<TableDto>> UnseatAsync(long guildId, Guid tableId, long userId, CancellationToken ct = default) =>
        SendAsync<TableDto>(http.DeleteAsync($"/guilds/{guildId}/game-nights/tables/{tableId}/seats/{userId}", ct), ct);

    /// <summary>Records (or clears) a table's Discord thread.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="tableId">The table.</param>
    /// <param name="threadId">The thread id, or null to forget a deleted thread.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The table, or a display-ready error.</returns>
    public Task<ApiResult<TableDto>> SetTableThreadAsync(long guildId, Guid tableId, long? threadId, CancellationToken ct = default) =>
        SendAsync<TableDto>(http.PutAsJsonAsync($"/guilds/{guildId}/game-nights/tables/{tableId}/thread", new SetTableThreadRequest(threadId), ct), ct);

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
                return new ApiResult<T>(await response.Content.ReadFromJsonAsync<T>(ct), null);
            }

            string? error = null;
            try
            {
                error = (await response.Content.ReadFromJsonAsync<ErrorResponse>(ct))?.Error;
            }
            catch
            {
                // Non-JSON error body; fall through to the status-code message.
            }

            return new ApiResult<T>(
                default, error ?? $"API error {(int)response.StatusCode}.", response.StatusCode == System.Net.HttpStatusCode.NotFound);
        }
    }
}
