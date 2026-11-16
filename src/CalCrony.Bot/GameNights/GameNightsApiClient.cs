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
