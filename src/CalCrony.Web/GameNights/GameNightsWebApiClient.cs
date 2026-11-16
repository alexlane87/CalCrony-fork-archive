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

    /// <summary>A server's active games.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The games, or the failure.</returns>
    public Task<ApiResult<List<GameDto>>> ListGamesAsync(long guildId, CancellationToken ct = default) =>
        SendAsync<List<GameDto>>(http.GetAsync($"/guilds/{guildId}/game-nights/games", ct), ct);

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
