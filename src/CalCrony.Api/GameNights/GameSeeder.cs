using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalCrony.Api.Data;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;

namespace CalCrony.Api.GameNights;

/// <summary>One game in the config file: a <see cref="GameSpec"/> plus the file's version of it.</summary>
/// <param name="Version">Bump this when the entry changes; servers whose copy has not been edited by
/// hand pick the change up the next time the file is applied.</param>
/// <param name="Key">The stable key.</param>
/// <param name="Name">The game's name.</param>
/// <param name="ResultType">How its result is recorded.</param>
/// <param name="MinSize">The fewest players.</param>
/// <param name="MaxSize">The most players.</param>
/// <param name="DefaultSize">The starting table size.</param>
/// <param name="RatingEnabled">Whether results feed a rating.</param>
/// <param name="RatingK">The rating K-factor.</param>
/// <param name="StartRating">The starting rating.</param>
/// <param name="Fields">The result questions.</param>
public sealed record SeedGame(
    int Version, string Key, string Name, GameResultType ResultType, int MinSize, int MaxSize, int DefaultSize,
    bool RatingEnabled, int RatingK, int StartRating, IReadOnlyList<GameFieldSpec> Fields)
{
    /// <summary>The definition without the version.</summary>
    /// <returns>The game definition.</returns>
    public GameSpec ToSpec() => new(
        Key, Name, ResultType, MinSize, MaxSize, DefaultSize, RatingEnabled, RatingK, StartRating, Fields);
}

/// <summary>The games config file.</summary>
/// <param name="Games">The games it defines.</param>
public sealed record SeedFile(IReadOnlyList<SeedGame> Games);

/// <summary>Loads the games config file: the one built into the API by default (so a fresh deployment
/// has Root and Arcs), or the file named by <c>GameNights:GamesFile</c>, which lets an operator add
/// or change games without a new image. Adding a game is editing that JSON.</summary>
public static class GameSeedFile
{
    private const string EmbeddedName = "CalCrony.Api.GameNights.games.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads and validates the file.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The validated file.</returns>
    /// <exception cref="InvalidOperationException">When the file is missing, unreadable or invalid.</exception>
    public static SeedFile Load(IConfiguration configuration)
    {
        var path = configuration["GameNights:GamesFile"];
        string json;
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"GameNights:GamesFile points at \"{path}\", which does not exist.");
            }

            json = File.ReadAllText(path);
        }
        else
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedName)
                ?? throw new InvalidOperationException("The built-in games file is missing from the API assembly.");
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }

        return Parse(json);
    }

    /// <summary>Parses and validates a games file.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The validated file.</returns>
    /// <exception cref="InvalidOperationException">When the text is not valid JSON for the format, or a game
    /// breaks the rules (the message lists every problem).</exception>
    public static SeedFile Parse(string json)
    {
        SeedFile? file;
        try
        {
            file = JsonSerializer.Deserialize<SeedFile>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The games file is not valid: {ex.Message}", ex);
        }

        if (file?.Games is null)
        {
            throw new InvalidOperationException("The games file has no \"games\" list.");
        }

        var problems = new List<string>();
        foreach (var game in file.Games)
        {
            problems.AddRange(GameRules.Validate(game.ToSpec()).Select(p => $"{game.Key}: {p}"));
        }

        problems.AddRange(file.Games.GroupBy(g => g.Key).Where(g => g.Count() > 1).Select(g => $"{g.Key}: listed twice."));
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The games file is not valid: " + string.Join(" ", problems));
        }

        return file;
    }
}

/// <summary>Applies the games file to a server, idempotently: a missing game is created; an existing
/// seeded game is refreshed only when the file carries a newer version of it AND nobody has edited it
/// by hand; everything else is left exactly as it is. Running it twice changes nothing the second time.</summary>
/// <param name="db">The database context.</param>
public sealed class GameSeeder(CalCronyDbContext db)
{
    /// <summary>Applies the file to one server.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="file">The validated file.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many games were created, refreshed and left alone.</returns>
    public async Task<SeedGamesResponse> ApplyAsync(long guildId, SeedFile file, CancellationToken cancellationToken)
    {
        int created = 0, updated = 0, skipped = 0;
        var existing = await db.GameNightGames
            .Include(g => g.Fields)
            .Include(g => g.TypeLinks)
            .Where(g => g.GuildId == guildId)
            .ToListAsync(cancellationToken);

        foreach (var entry in file.Games)
        {
            var game = existing.FirstOrDefault(g => g.Key == entry.Key);
            if (game is null)
            {
                if (existing.Count >= GameRules.MaxGamesPerGuild)
                {
                    skipped++;
                    continue;
                }

                game = new GameNightGame { Id = Guid.NewGuid(), GuildId = guildId, SeedVersion = entry.Version };
                db.GameNightGames.Add(game);
                existing.Add(game);
                GameCatalog.Apply(game, entry.ToSpec(), includeLinks: false);
                created++;
            }
            else if (game.SeedVersion is { } have && entry.Version > have && !game.LocallyEdited)
            {
                GameCatalog.Apply(game, entry.ToSpec(), includeLinks: false);
                game.SeedVersion = entry.Version;
                updated++;
            }
            else
            {
                skipped++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new SeedGamesResponse(created, updated, skipped);
    }
}
