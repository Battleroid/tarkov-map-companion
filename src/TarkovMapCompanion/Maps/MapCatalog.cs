using System.Reflection;
using System.Text.Json;

namespace TarkovMapCompanion.Maps;

/// <summary>
/// The set of maps the app can render, loaded from tarkov.dev's <c>maps.json</c>.
/// </summary>
/// <remarks>
/// A snapshot is embedded in the assembly so the app is fully usable offline and on first run.
/// Map geometry changes only when BSG reworks a map, so a stale snapshot degrades gracefully:
/// worst case a newly added map is missing until the catalog is refreshed.
/// </remarks>
public sealed class MapCatalog
{
    public const string SnapshotResourceName = "TarkovMapCompanion.Data.Snapshots.maps.json";

    /// <summary>Upstream source, for the optional refresh and for attribution.</summary>
    public const string SourceUrl = "https://raw.githubusercontent.com/the-hideout/tarkov-dev/main/src/data/maps.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, GameMap> _byName;

    private MapCatalog(IReadOnlyList<GameMap> maps)
    {
        Maps = maps;
        _byName = maps.ToDictionary(m => m.NormalizedName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Renderable maps, alphabetical by display name.</summary>
    public IReadOnlyList<GameMap> Maps { get; }

    public GameMap? Find(string? normalizedName) =>
        normalizedName is not null && _byName.TryGetValue(normalizedName, out var map) ? map : null;

    /// <summary>
    /// Looks up a map, falling back to the first available one so callers never have to handle a
    /// null map just because a settings file names something that no longer exists.
    /// </summary>
    public GameMap Resolve(string? normalizedName) => Find(normalizedName) ?? Maps[0];

    /// <summary>
    /// Looks up a map by something the game called it: a <c>nameId</c>, a scene token, or a
    /// normalized name. Null when the token means nothing here.
    /// </summary>
    /// <remarks>
    /// Null rather than a fallback, unlike <see cref="Resolve"/>. This one is driven by the game
    /// log, where "I do not recognize that" has to stay distinguishable from "it is Factory":
    /// falling back would switch the user's map to whatever sorts first every time BSG adds a
    /// location.
    /// </remarks>
    public GameMap? ResolveByNameId(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        // The table first, then the raw token. That order matters for Ground Zero, whose upstream
        // nameId variants would otherwise miss entirely, and it costs nothing for the maps whose
        // scene token happens to be their normalized name already.
        return Find(MapNameIds.NormalizedNameFor(token)) ?? Find(token.Trim());
    }

    public static MapCatalog LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(SnapshotResourceName)
            ?? throw new InvalidOperationException(
                $"embedded map snapshot '{SnapshotResourceName}' is missing from the assembly");

        return Load(stream);
    }

    public static MapCatalog Load(Stream json)
    {
        var groups = JsonSerializer.Deserialize<List<MapGroupJson>>(json, JsonOptions)
            ?? throw new InvalidOperationException("map catalog JSON did not parse to a list");

        return FromGroups(groups);
    }

    public static MapCatalog Parse(string json)
    {
        var groups = JsonSerializer.Deserialize<List<MapGroupJson>>(json, JsonOptions)
            ?? throw new InvalidOperationException("map catalog JSON did not parse to a list");

        return FromGroups(groups);
    }

    private static MapCatalog FromGroups(List<MapGroupJson> groups)
    {
        var maps = new List<GameMap>();

        foreach (var group in groups)
        {
            // Each group also carries 2D/3D still images we have no renderer for.
            var variant = group.Maps.FirstOrDefault(v =>
                string.Equals(v.Projection, "interactive", StringComparison.OrdinalIgnoreCase));

            if (variant is null)
                continue;

            try
            {
                maps.Add(new GameMap(group, variant));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // One unprojectable map should not take the catalog down with it.
                Console.Error.WriteLine($"maps: skipping '{group.NormalizedName}': {ex.Message}");
            }
        }

        if (maps.Count == 0)
            throw new InvalidOperationException("map catalog contained no usable interactive maps");

        maps.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return new MapCatalog(maps);
    }
}
