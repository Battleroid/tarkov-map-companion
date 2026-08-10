using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using TarkovMapCompanion.Data.Models;
using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.Data;

/// <summary>
/// Supplies POI data (extracts, spawns, loot, hazards) for every map.
/// </summary>
/// <remarks>
/// <para>
/// Resolution order is deliberate: <b>embedded snapshot, then disk cache, then network</b>. The app
/// must be fully usable with no connection at all -- someone mid-raid is not helped by a spinner --
/// so it always starts from data it already has and upgrades in the background.
/// </para>
/// <para>
/// Source is <c>json.tarkov.dev</c>, the pre-baked payload tarkov.dev's own site reads. The
/// documented GraphQL API would be the obvious choice, but it returned
/// <c>422 GraphQL server unavailable</c> for every query across the whole of development while
/// this endpoint served fine. If GraphQL is ever needed, note it exposes a couple of fields this
/// payload lacks (notably an extract's required transfer item).
/// </para>
/// </remarks>
public sealed class MapDataStore
{
    public const string DataUrl = "https://json.tarkov.dev/regular/maps";
    public const string TranslationsUrl = "https://json.tarkov.dev/regular/maps_en";

    private const string SnapshotResourceName = "TarkovMapCompanion.Data.Snapshots.mapdata.json.gz";
    private const string CacheFileName = "mapdata.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private readonly string _cachePath;

    private MapDataDocument _document = new();

    public MapDataStore(AppSettings settings, HttpClient? httpClient = null, string? cacheDirectory = null)
    {
        _settings = settings;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _cachePath = Path.Combine(cacheDirectory ?? AppPaths.CacheDirectory, CacheFileName);
    }

    /// <summary>Where the currently loaded data came from, for the status bar.</summary>
    public string Origin { get; private set; } = "none";

    public DateTimeOffset? FetchedAt => _document.FetchedAt;

    /// <summary>Raised when a background refresh has replaced the data.</summary>
    public event EventHandler? Updated;

    /// <summary>
    /// Loads the best data available without touching the network. Fast enough for startup.
    /// </summary>
    public void LoadLocal()
    {
        if (TryLoadFrom(() => ReadCacheFile(), "disk cache"))
            return;

        if (TryLoadFrom(ReadEmbeddedSnapshot, "bundled snapshot"))
            return;

        Origin = "unavailable";
    }

    /// <summary>
    /// Refreshes from the network if the local copy is older than the configured interval.
    /// Failures are silent by design: stale data beats no data.
    /// </summary>
    public async Task RefreshIfStaleAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.AllowNetwork)
            return;

        var age = DateTimeOffset.UtcNow - (_document.FetchedAt ?? DateTimeOffset.MinValue);
        if (age < TimeSpan.FromHours(_settings.DataRefreshIntervalHours))
            return;

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fetches fresh data regardless of age. Returns false if the fetch failed.</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var document = await FetchAsync(_http, cancellationToken).ConfigureAwait(false);

            _document = document;
            Origin = "tarkov.dev";

            await WriteCacheAsync(document, cancellationToken).ConfigureAwait(false);

            Updated?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return false;
        }
    }

    /// <summary>POI data for a map, or null when this snapshot does not cover it.</summary>
    public MapPoiData? ForMap(string normalizedName) =>
        _document.Maps.Values.FirstOrDefault(m =>
            string.Equals(m.NormalizedName, normalizedName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves a localization key such as <c>EXFIL_ZB013</c> to readable text.</summary>
    public string Translate(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return "";

        return _document.Translations.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : key;
    }

    /// <summary>Maps a BSG map id to a normalized name, for transit destinations.</summary>
    public string? NormalizedNameForId(string? mapId) =>
        mapId is not null && _document.Maps.TryGetValue(mapId, out var map) ? map.NormalizedName : null;

    /// <summary>
    /// Maps a game-log <c>Location:</c> value to a normalized name, when this data knows it.
    /// </summary>
    /// <remarks>
    /// The hardcoded table in <c>MapNameIds</c> is checked first at the call site, because it also
    /// folds variants onto the maps that are actually shipped. This is the path that keeps working
    /// when BSG renames a location between app releases, since the data refreshes and the table
    /// does not.
    /// </remarks>
    public string? NormalizedNameForNameId(string? nameId) =>
        string.IsNullOrWhiteSpace(nameId)
            ? null
            : _document.Maps.Values
                .FirstOrDefault(m => string.Equals(m.NameId, nameId, StringComparison.OrdinalIgnoreCase))
                ?.NormalizedName;

    /// <summary>
    /// Maps a BSG map id to the game's location id.
    /// </summary>
    /// <remarks>
    /// The step that lets quest zones, which are keyed by BSG map id, land on a shipped map:
    /// resolving the id to a normalized name is not enough, because upstream has seventeen maps to
    /// this app's thirteen and the extra four are variants. Going via the location id folds them.
    /// </remarks>
    public string? NameIdForId(string? mapId) =>
        mapId is not null && _document.Maps.TryGetValue(mapId, out var map) ? map.NameId : null;

    /// <summary>
    /// The bundled snapshot, unfiltered. For tests that need to check the data itself rather than
    /// whatever happens to be cached on this machine.
    /// </summary>
    public static MapDataDocument? EmbeddedSnapshot() => ReadEmbeddedSnapshot();

    // ---- Fetching -----------------------------------------------------------

    /// <summary>
    /// Downloads and merges the data and translation documents. Public so the snapshot generator
    /// uses exactly the same code path the app does.
    /// </summary>
    public static async Task<MapDataDocument> FetchAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        var dataTask = http.GetStringAsync(DataUrl, cancellationToken);
        var translationsTask = http.GetStringAsync(TranslationsUrl, cancellationToken);

        await Task.WhenAll(dataTask, translationsTask).ConfigureAwait(false);

        // Both documents wrap their payload in a "data" envelope.
        var data = JsonSerializer.Deserialize<DataEnvelope<MapDataDocument>>(dataTask.Result, JsonOptions)?.Data
                   ?? throw new JsonException("map data document had no 'data' member");

        var translations = JsonSerializer
            .Deserialize<DataEnvelope<Dictionary<string, string>>>(translationsTask.Result, JsonOptions)?.Data;

        data.Translations = translations ?? [];
        data.FetchedAt = DateTimeOffset.UtcNow;

        return data;
    }

    /// <summary>
    /// Drops what is never drawn so the snapshot is small enough to embed. Loose loot is the bulk
    /// of the payload -- over 6,000 points across the maps -- and is simply not modeled, so it
    /// falls away for free when the document round-trips through these types: 9.5 MB in,
    /// about 0.36 MB gzipped out.
    /// </summary>
    public static MapDataDocument Slim(MapDataDocument document) => document;

    /// <summary>
    /// The raid length in minutes for a map, when known. Used to bound the player's trail.
    /// </summary>
    public int? RaidDurationMinutes(string normalizedName) => ForMap(normalizedName)?.RaidDuration;

    /// <summary>Readable name for a loot-container type id.</summary>
    public string LootContainerName(string? id) =>
        id is not null && _document.LootContainers.TryGetValue(id, out var c) ? Translate(c.Name) : "Container";

    /// <summary>Readable name for a stationary-weapon id.</summary>
    public string StationaryWeaponName(string? id) =>
        id is not null && _document.StationaryWeapons.TryGetValue(id, out var w) ? Translate(w.Name) : "Mounted gun";

    /// <summary>Readable name for a mob id such as <c>bossBully</c>.</summary>
    public string MobName(string? id) =>
        id is not null && _document.Mobs.TryGetValue(id, out var m) ? Translate(m.Name) : Translate(id);

    // ---- Storage ------------------------------------------------------------

    private bool TryLoadFrom(Func<MapDataDocument?> read, string origin)
    {
        try
        {
            var document = read();
            if (document is null || document.Maps.Count == 0)
                return false;

            _document = document;
            Origin = origin;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"map data: could not read {origin}: {ex.Message}");
            return false;
        }
    }

    private MapDataDocument? ReadCacheFile()
    {
        if (!File.Exists(_cachePath))
            return null;

        using var stream = File.OpenRead(_cachePath);
        return JsonSerializer.Deserialize<MapDataDocument>(stream, JsonOptions);
    }

    private static MapDataDocument? ReadEmbeddedSnapshot()
    {
        using var compressed = Assembly.GetExecutingAssembly().GetManifestResourceStream(SnapshotResourceName);
        if (compressed is null)
            return null;

        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<MapDataDocument>(gzip, JsonOptions);
    }

    private async Task WriteCacheAsync(MapDataDocument document, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);

            var temp = $"{_cachePath}.{Environment.ProcessId}.tmp";
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);

            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Refetching next launch is cheaper than failing here.
        }
    }

    private sealed class DataEnvelope<T>
    {
        [System.Text.Json.Serialization.JsonPropertyName("data")]
        public T? Data { get; set; }
    }
}
