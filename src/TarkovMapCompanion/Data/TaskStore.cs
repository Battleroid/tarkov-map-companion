using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovMapCompanion.Data.Models;
using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.Data;

/// <summary>
/// Supplies Tarkov's quests and, where they have one, the places on a map they happen.
/// </summary>
/// <remarks>
/// <para>
/// Same shape and same rules as <see cref="MapDataStore"/>: embedded snapshot, then disk cache,
/// then an optional background refresh. Quests change on a wipe rather than daily, so a stale
/// snapshot costs at most a few missing tasks.
/// </para>
/// <para>
/// The projection to the slim model happens in <see cref="FetchAsync"/> rather than in the
/// generator, so the snapshot the app ships and the one it downloads are built by the same code.
/// </para>
/// </remarks>
public sealed class TaskStore
{
    public const string DataUrl = "https://json.tarkov.dev/regular/tasks";
    public const string TranslationsUrl = "https://json.tarkov.dev/regular/tasks_en";
    public const string TradersUrl = "https://json.tarkov.dev/regular/traders";
    public const string TraderTranslationsUrl = "https://json.tarkov.dev/regular/traders_en";

    /// <summary>
    /// Item names only, which is 385 KB against the 16 MB the full item payload costs.
    /// </summary>
    /// <remarks>
    /// The same flat shape as the other translation documents. Only the few hundred names tasks
    /// actually reference survive into the snapshot.
    /// </remarks>
    public const string ItemTranslationsUrl = "https://json.tarkov.dev/regular/items_en";

    private const string SnapshotResourceName = "TarkovMapCompanion.Data.Snapshots.tasks.json.gz";
    private const string CacheFileName = "tasks.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private readonly string _cachePath;

    private TaskDocument _document = new();

    public TaskStore(AppSettings settings, HttpClient? httpClient = null, string? cacheDirectory = null)
    {
        _settings = settings;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _cachePath = Path.Combine(cacheDirectory ?? AppPaths.CacheDirectory, CacheFileName);
    }

    /// <summary>Where the currently loaded data came from, for the preferences screen.</summary>
    public string Origin { get; private set; } = "none";

    public DateTimeOffset? FetchedAt => _document.FetchedAt;

    /// <summary>Every task, in no particular order.</summary>
    public IReadOnlyList<TaskData> Tasks => _document.Tasks;

    /// <summary>Raised when a background refresh has replaced the data.</summary>
    public event EventHandler? Updated;

    public void LoadLocal()
    {
        if (TryLoadFrom(ReadCacheFile, "disk cache"))
            return;

        if (TryLoadFrom(ReadEmbeddedSnapshot, "bundled snapshot"))
            return;

        Origin = "unavailable";
    }

    public async Task RefreshIfStaleAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.AllowNetwork)
            return;

        var age = DateTimeOffset.UtcNow - (_document.FetchedAt ?? DateTimeOffset.MinValue);
        if (age < TimeSpan.FromHours(_settings.DataRefreshIntervalHours))
            return;

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

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

    /// <summary>A task by id, or null.</summary>
    public TaskData? Find(string? id) =>
        id is null ? null : _document.Tasks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    // ---- Fetching -----------------------------------------------------------

    /// <summary>
    /// Downloads the four documents and projects them to the slim model.
    /// </summary>
    /// <remarks>
    /// Public so the snapshot generator uses exactly the code path the app does. Traders come along
    /// because task records name their trader by id and nothing else resolves it: the id is not in
    /// the task translations, and the traders document is 6 KB.
    /// </remarks>
    public static async Task<TaskDocument> FetchAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        var tasksTask = http.GetStringAsync(DataUrl, cancellationToken);
        var stringsTask = http.GetStringAsync(TranslationsUrl, cancellationToken);
        var tradersTask = http.GetStringAsync(TradersUrl, cancellationToken);
        var traderStringsTask = http.GetStringAsync(TraderTranslationsUrl, cancellationToken);
        var itemStringsTask = http.GetStringAsync(ItemTranslationsUrl, cancellationToken);

        await Task.WhenAll(tasksTask, stringsTask, tradersTask, traderStringsTask, itemStringsTask)
            .ConfigureAwait(false);

        var upstream = Unwrap<UpstreamTaskDocument>(tasksTask.Result)
                       ?? throw new JsonException("task document had no 'data' member");

        var strings = Unwrap<Dictionary<string, string>>(stringsTask.Result) ?? [];
        var traders = Unwrap<Dictionary<string, UpstreamTrader>>(tradersTask.Result) ?? [];
        var traderStrings = Unwrap<Dictionary<string, string>>(traderStringsTask.Result) ?? [];
        var itemStrings = Unwrap<Dictionary<string, string>>(itemStringsTask.Result) ?? [];

        return Project(upstream, strings, traders, traderStrings, itemStrings);
    }

    private static T? Unwrap<T>(string json) =>
        JsonSerializer.Deserialize<DataEnvelope<T>>(json, JsonOptions) is { Data: { } data } ? data : default;

    /// <summary>Turns the upstream shape into the one the app draws from.</summary>
    internal static TaskDocument Project(
        UpstreamTaskDocument upstream,
        IReadOnlyDictionary<string, string> strings,
        IReadOnlyDictionary<string, UpstreamTrader> traders,
        IReadOnlyDictionary<string, string> traderStrings,
        IReadOnlyDictionary<string, string>? itemStrings = null)
    {
        var itemNames = itemStrings ?? new Dictionary<string, string>();

        // Keyed "<id> Name", the same convention the map and task documents use. An id with no
        // name is dropped rather than shown as a hex blob.
        TaskItemData? Item(string? id) =>
            id is { Length: > 0 } && itemNames.TryGetValue(id + " Name", out var found) && found.Length > 0
                ? new TaskItemData { Id = id, Name = found }
                : null;

        string Text(string? key) =>
            key is not null && strings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : key ?? "";

        string TraderName(string? id)
        {
            if (id is null || !traders.TryGetValue(id, out var trader))
                return id ?? "";

            return trader.Name is { } key && traderStrings.TryGetValue(key, out var name) && name.Length > 0
                ? name
                : trader.NormalizedName ?? id;
        }

        var document = new TaskDocument { FetchedAt = DateTimeOffset.UtcNow };

        foreach (var task in upstream.Tasks.Values)
        {
            var projected = new TaskData
            {
                Id = task.Id ?? "",
                Name = Text(task.Name),
                NormalizedName = task.NormalizedName ?? "",
                Trader = TraderName(task.Trader),
                MinPlayerLevel = task.MinPlayerLevel ?? 0,
                KappaRequired = task.KappaRequired ?? false,
                LightkeeperRequired = task.LightkeeperRequired ?? false,

                // "Any" is the overwhelming majority and means no restriction, so it is dropped
                // rather than stored 498 times.
                Faction = string.Equals(task.FactionName, "Any", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : task.FactionName,
                WikiLink = task.WikiLink,
                MapId = task.Map,
                Requires = (task.TaskRequirements ?? [])
                    .Select(r => r.Task)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Select(id => id!)
                    .ToList(),
            };

            foreach (var group in task.NeededKeys ?? [])
            {
                var keys = (group.Keys ?? [])
                    .Select(Item)
                    .Where(k => k is not null)
                    .Select(k => k!)
                    .ToList();

                if (group.Map is { Length: > 0 } keyMap && keys.Count > 0)
                    projected.Keys.Add(new TaskKeyData { MapId = keyMap, Keys = keys });
            }

            foreach (var objective in task.Objectives ?? [])
                projected.Objectives.Add(ProjectObjective(objective, Text, Item));

            document.Tasks.Add(projected);
        }

        document.Tasks.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return document;
    }

    private static TaskObjectiveData ProjectObjective(
        UpstreamObjective objective,
        Func<string?, string> text,
        Func<string?, TaskItemData?> item)
    {
        var projected = new TaskObjectiveData
        {
            Id = objective.Id ?? "",
            Type = objective.Type ?? "",
            Description = text(objective.Description),
            Optional = objective.Optional ?? false,
            Count = objective.Count,
            FoundInRaid = objective.FoundInRaid ?? false,
        };

        // The marker you plant is worth naming even when nothing else is: placing an MS2000 is a
        // different errand from placing a WI-FI camera.
        if (item(objective.MarkerItem) is { } marker)
            projected.Items.Add(marker);

        if (objective.Items is { Count: > 0 } wanted && wanted.Count <= TaskObjectiveData.MaxNamedItems)
        {
            foreach (var found in wanted.Select(item).Where(i => i is not null).Select(i => i!))
            {
                if (!projected.Items.Any(i => string.Equals(i.Id, found.Id, StringComparison.Ordinal)))
                    projected.Items.Add(found);
            }
        }

        foreach (var zone in objective.Zones ?? [])
        {
            if (zone.Map is not { Length: > 0 } map || zone.Position is null)
                continue;

            projected.Points.Add(new TaskPointData
            {
                MapId = map,
                X = Round(zone.Position.X),
                Y = Round(zone.Position.Y),
                Z = Round(zone.Position.Z),
                Outline = zone.Outline is { Count: > 2 }
                    ? zone.Outline.Select(p => new[] { Round(p.X), Round(p.Z) }).ToList()
                    : null,
            });
        }

        // Quest items list every spot they can spawn in rather than the one they are in.
        foreach (var location in objective.PossibleLocations ?? [])
        {
            if (location.Map is not { Length: > 0 } map)
                continue;

            foreach (var position in location.Positions ?? [])
            {
                projected.Points.Add(new TaskPointData
                {
                    MapId = map,
                    X = Round(position.X),
                    Y = Round(position.Y),
                    Z = Round(position.Z),
                    OneOf = true,
                });
            }
        }

        return projected;
    }

    /// <summary>
    /// Centimeters are plenty.
    /// </summary>
    /// <remarks>
    /// Upstream carries seven decimal places, which is sub-micron and costs about a fifth of the
    /// compressed snapshot to store.
    /// </remarks>
    private static double Round(double value) => Math.Round(value, 2);

    // ---- Storage ------------------------------------------------------------

    private bool TryLoadFrom(Func<TaskDocument?> read, string origin)
    {
        try
        {
            var document = read();
            if (document is null || document.Tasks.Count == 0)
                return false;

            _document = document;
            Origin = origin;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"tasks: could not read {origin}: {ex.Message}");
            return false;
        }
    }

    private TaskDocument? ReadCacheFile()
    {
        if (!File.Exists(_cachePath))
            return null;

        using var stream = File.OpenRead(_cachePath);
        return JsonSerializer.Deserialize<TaskDocument>(stream, JsonOptions);
    }

    private static TaskDocument? ReadEmbeddedSnapshot()
    {
        using var compressed = Assembly.GetExecutingAssembly().GetManifestResourceStream(SnapshotResourceName);
        if (compressed is null)
            return null;

        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<TaskDocument>(gzip, JsonOptions);
    }

    /// <summary>The bundled snapshot, for tests and for the generator to compare against.</summary>
    public static TaskDocument? EmbeddedSnapshot() => ReadEmbeddedSnapshot();

    private async Task WriteCacheAsync(TaskDocument document, CancellationToken cancellationToken)
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
        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }
}

// ---- Upstream shapes --------------------------------------------------------

/// <summary>
/// Only the parts of <c>regular/tasks</c> that survive into the snapshot.
/// </summary>
/// <remarks>
/// Unlisted members are dropped on the way through, which is most of the payload: reward graphs,
/// item id lists, image links and fail conditions come to nine tenths of the 2.2 MB.
/// </remarks>
public sealed class UpstreamTaskDocument
{
    [JsonPropertyName("tasks")] public Dictionary<string, UpstreamTask> Tasks { get; set; } = [];
}

public sealed class UpstreamTask
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("normalizedName")] public string? NormalizedName { get; set; }
    [JsonPropertyName("trader")] public string? Trader { get; set; }
    [JsonPropertyName("wikiLink")] public string? WikiLink { get; set; }
    [JsonPropertyName("minPlayerLevel")] public int? MinPlayerLevel { get; set; }
    [JsonPropertyName("kappaRequired")] public bool? KappaRequired { get; set; }
    [JsonPropertyName("lightkeeperRequired")] public bool? LightkeeperRequired { get; set; }
    [JsonPropertyName("factionName")] public string? FactionName { get; set; }
    [JsonPropertyName("map")] public string? Map { get; set; }
    [JsonPropertyName("taskRequirements")] public List<UpstreamTaskRequirement>? TaskRequirements { get; set; }
    [JsonPropertyName("objectives")] public List<UpstreamObjective>? Objectives { get; set; }
    [JsonPropertyName("neededKeys")] public List<UpstreamNeededKeys>? NeededKeys { get; set; }
}

public sealed class UpstreamNeededKeys
{
    [JsonPropertyName("map")] public string? Map { get; set; }
    [JsonPropertyName("keys")] public List<string>? Keys { get; set; }
}

public sealed class UpstreamTaskRequirement
{
    [JsonPropertyName("task")] public string? Task { get; set; }
}

public sealed class UpstreamObjective
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("optional")] public bool? Optional { get; set; }
    [JsonPropertyName("count")] public int? Count { get; set; }
    [JsonPropertyName("foundInRaid")] public bool? FoundInRaid { get; set; }
    [JsonPropertyName("items")] public List<string>? Items { get; set; }
    [JsonPropertyName("markerItem")] public string? MarkerItem { get; set; }
    [JsonPropertyName("zones")] public List<UpstreamZone>? Zones { get; set; }
    [JsonPropertyName("possibleLocations")] public List<UpstreamPossibleLocation>? PossibleLocations { get; set; }
}

public sealed class UpstreamZone
{
    [JsonPropertyName("map")] public string? Map { get; set; }
    [JsonPropertyName("position")] public Vec3Data? Position { get; set; }
    [JsonPropertyName("outline")] public List<Vec3Data>? Outline { get; set; }
}

public sealed class UpstreamPossibleLocation
{
    [JsonPropertyName("map")] public string? Map { get; set; }
    [JsonPropertyName("positions")] public List<Vec3Data>? Positions { get; set; }
}

public sealed class UpstreamTrader
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("normalizedName")] public string? NormalizedName { get; set; }
}
