using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovMapCompanion.Diagnostics;

namespace TarkovMapCompanion.Data;

/// <summary>
/// One season-1 "KORD BREACH" TerraGroup document spawn point.
/// </summary>
/// <remarks>
/// The source is Chinese-only, so <see cref="NameZh"/> doubles as the English name at the display
/// site. Coordinates are game world (x, z); height is not supplied.
/// </remarks>
public sealed class SeasonDocumentPoint
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("map")] public string Map { get; set; } = "";
    [JsonPropertyName("nameZh")] public string? NameZh { get; set; }
    [JsonPropertyName("typeZh")] public string? TypeZh { get; set; }
    [JsonPropertyName("titleZh")] public string? TitleZh { get; set; }
    [JsonPropertyName("floor")] public string? Floor { get; set; }
    [JsonPropertyName("keyRequiredZh")] public string? KeyRequiredZh { get; set; }

    /// <summary>False when nobody has confirmed the spawn in game; drives the "?" styling.</summary>
    [JsonPropertyName("confirmed")] public bool Confirmed { get; set; }

    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("z")] public double Z { get; set; }
}

/// <summary>Whole snapshot of season document spawns; embedded read-only data.</summary>
public sealed class SeasonDocumentsDocument
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("generatedAt")] public string? GeneratedAt { get; set; }
    [JsonPropertyName("points")] public List<SeasonDocumentPoint> Points { get; set; } = [];
}

/// <summary>
/// Loads the bundled season document spawn snapshot, keyed by map.
/// </summary>
/// <remarks>
/// Embedded-only: the snapshot ships with the app and has no user-override file, unlike
/// <see cref="ExtractNotesStore"/>. A missing or unreadable resource leaves an empty store so the
/// layer simply draws nothing rather than taking the map down.
/// </remarks>
public sealed class SeasonDocumentStore
{
    private const string SnapshotResourceName = "TarkovMapCompanion.Data.Snapshots.season-documents.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>Guards the one-time warning about a missing snapshot.</summary>
    private static int _missingResourceLogged;

    private readonly Dictionary<string, List<SeasonDocumentPoint>> _byMap = new(StringComparer.Ordinal);

    public SeasonDocumentStore() => Load();

    private SeasonDocumentStore(SeasonDocumentsDocument? document) => Merge(document);

    /// <summary>How many document spawns this map has. 0 when it has none.</summary>
    public int CountFor(string map) =>
        _byMap.TryGetValue(NormalizeMap(map), out var points) ? points.Count : 0;

    /// <summary>The document spawns for a map, or empty. Never null.</summary>
    public IReadOnlyList<SeasonDocumentPoint> FindFor(string map) =>
        _byMap.TryGetValue(NormalizeMap(map), out var points) ? points : [];

    /// <summary>
    /// Builds a store from an in-memory snapshot. Tests use this so they do not depend on the
    /// bundled resource; production always goes through the parameterless constructor.
    /// </summary>
    internal static SeasonDocumentStore FromJson(string json) =>
        new(JsonSerializer.Deserialize<SeasonDocumentsDocument>(json, JsonOptions));

    private void Load()
    {
        var document = ReadEmbedded();

        if (document is null && Interlocked.Exchange(ref _missingResourceLogged, 1) == 0)
            Log.Warn("season documents: bundled snapshot missing or unreadable; the layer will be empty");

        Merge(document);
    }

    private void Merge(SeasonDocumentsDocument? document)
    {
        if (document is null)
            return;

        foreach (var point in document.Points)
        {
            if (string.IsNullOrWhiteSpace(point.Map) || string.IsNullOrWhiteSpace(point.Id))
                continue;

            var key = NormalizeMap(point.Map);
            if (!_byMap.TryGetValue(key, out var bucket))
                _byMap[key] = bucket = [];

            bucket.Add(point);
        }
    }

    /// <summary>
    /// Map match key. The snapshot uses tarkov.dev-style slugs ("ground-zero", "the-lab") while the
    /// catalog's display names are prose ("Ground Zero", "The Lab"), so both sides are folded to
    /// lowercase alphanumerics before comparison.
    /// </summary>
    internal static string NormalizeMap(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        var length = 0;

        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
                buffer[length++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..length]);
    }

    private static SeasonDocumentsDocument? ReadEmbedded()
    {
        try
        {
            using var stream = System.Reflection.Assembly
                .GetExecutingAssembly()
                .GetManifestResourceStream(SnapshotResourceName);

            return stream is null ? null : JsonSerializer.Deserialize<SeasonDocumentsDocument>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            Log.Warn($"season documents: bundled snapshot unreadable: {ex.Message}");
            return null;
        }
    }
}
