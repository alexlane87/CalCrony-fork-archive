namespace CalCrony.Bot.GameNights;

/// <summary>The one registration call the bot's Program.cs makes for Game Nights. The commands are
/// discovered with the rest of the assembly's modules; each is guarded by
/// <see cref="RequireGameNightsAttribute"/>, so with the flag off they answer politely and do nothing.</summary>
public static class GameNightsBotExtensions
{
    /// <summary>The configuration key of the feature flag (shared with the API).</summary>
    public const string EnabledKey = "GameNights:Enabled";

    /// <summary>Whether Game Nights is switched on.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>True when <c>GameNights:Enabled</c> is true.</returns>
    public static bool IsGameNightsEnabled(this IConfiguration configuration) =>
        configuration.GetValue(EnabledKey, false);

    /// <summary>Registers the Game Nights services.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddGameNights(this IServiceCollection services)
    {
        services.AddHttpClient<GameNightsApiClient>((sp, http) =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            http.BaseAddress = new Uri(config["Api:BaseUrl"] ?? "http://localhost:8080");
            http.DefaultRequestHeaders.Add("X-Api-Key", config["Api:ApiKey"] ?? "");
        });
        services.AddSingleton<BotTier>();
        return services;
    }
}
