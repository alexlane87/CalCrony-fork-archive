namespace CalCrony.Api.GameNights;

/// <summary>The single entry point the API's Program.cs calls (two lines there). Everything is
/// gated by <c>GameNights:Enabled</c>, default false, so a deployment that has not opted in
/// serves no Game Nights routes. The tables are created by the migration regardless; they are
/// simply empty.</summary>
public static class GameNightsServiceCollectionExtensions
{
    /// <summary>The configuration key of the feature flag.</summary>
    public const string EnabledKey = "GameNights:Enabled";

    /// <summary>Whether Game Nights is switched on.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>True when <c>GameNights:Enabled</c> is true.</returns>
    public static bool IsGameNightsEnabled(this IConfiguration configuration) => configuration.GetValue(EnabledKey, false);

    /// <summary>Registers the Game Nights services when the flag is on.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddGameNights(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration.IsGameNightsEnabled())
        {
            services.AddScoped<TierResolver>();
        }

        return services;
    }

    /// <summary>Maps the Game Nights routes when the flag is on.</summary>
    /// <param name="app">The web application.</param>
    /// <returns>The web application.</returns>
    public static WebApplication MapGameNights(this WebApplication app)
    {
        if (app.Configuration.IsGameNightsEnabled())
        {
            app.MapGameNightsEndpoints();
        }

        return app;
    }
}
