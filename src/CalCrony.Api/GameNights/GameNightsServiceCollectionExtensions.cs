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
            services.AddScoped<GameSeeder>();
        }

        return services;
    }

    /// <summary>Maps the Game Nights routes when the flag is on, and arranges for the games config file
    /// to be applied to the servers listed in <c>GameNights:SeedGuildIds</c> (comma-separated) once the
    /// host has started (so the migrations have run). Seeding is idempotent and never stops the API
    /// starting: a problem is logged.</summary>
    /// <param name="app">The web application.</param>
    /// <returns>The web application.</returns>
    public static WebApplication MapGameNights(this WebApplication app)
    {
        if (!app.Configuration.IsGameNightsEnabled())
        {
            return app;
        }

        app.MapGameNightsEndpoints();

        var guildIds = ParseGuildIds(app.Configuration["GameNights:SeedGuildIds"]);
        if (guildIds.Count > 0)
        {
            app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(() => SeedAsync(app, guildIds)));
        }

        return app;
    }

    /// <summary>Parses a comma-separated list of server ids; blanks and anything that is not a number are ignored.</summary>
    /// <param name="text">The configuration value.</param>
    /// <returns>The ids.</returns>
    public static IReadOnlyList<long> ParseGuildIds(string? text) =>
        [.. (text ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => long.TryParse(part, out var id) && id > 0 ? id : (long?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()];

    private static async Task SeedAsync(WebApplication app, IReadOnlyList<long> guildIds)
    {
        try
        {
            var file = GameSeedFile.Load(app.Configuration);
            foreach (var guildId in guildIds)
            {
                await using var scope = app.Services.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<GameSeeder>().ApplyAsync(guildId, file, CancellationToken.None);
                app.Logger.LogInformation(
                    "Game Nights games for guild {GuildId}: {Created} created, {Updated} updated, {Skipped} left alone.",
                    guildId, result.Created, result.Updated, result.Skipped);
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Game Nights could not apply the games file.");
        }
    }
}
