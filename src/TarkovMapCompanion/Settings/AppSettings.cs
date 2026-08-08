using System.Text.Json.Serialization;

namespace TarkovMapCompanion.Settings;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// How (and whether) the app removes screenshots from the watched folder.
/// Defaults to <see cref="Off"/>: this feature deletes the user's files, so it is opt-in.
/// </summary>
public enum CullMode
{
    /// <summary>Never remove anything.</summary>
    Off,

    /// <summary>Keep the <see cref="AppSettings.CullKeepCount"/> newest screenshots, remove the rest.</summary>
    KeepLatest,

    /// <summary>Remove each screenshot as soon as its position has been read.</summary>
    DeleteAfterRead,
}

/// <summary>
/// Which exits to show, framed around the run you are actually on.
/// </summary>
/// <remarks>
/// The "as" options are the useful ones in a raid: a PMC can use PMC and shared exits but never a
/// Scav-only one, so showing Scav exits is just clutter that costs you time to read past. The
/// "only" options exist for planning rather than playing.
/// </remarks>
public enum ExitFilter
{
    All,
    AsPmc,
    AsScav,
    PmcOnly,
    ScavOnly,
    SharedOnly,
}

/// <summary>What happens to a route marker once the player reaches it.</summary>
public enum WaypointArrival
{
    /// <summary>
    /// Show it as reached for one update, then drop it. The confirmation is the point: you find out
    /// you were counted as arriving, rather than watching a pin vanish and being left wondering
    /// whether you got close enough or whether you had misplaced it to begin with.
    /// </summary>
    MarkThenRemove,

    /// <summary>Drop it the moment the player is inside the radius.</summary>
    RemoveOnArrival,
}

/// <summary>
/// User preferences. Persisted as JSON; every member needs a sane default because a settings file
/// written by an older build will simply be missing the newer keys.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Bumped when a migration is needed. Not currently used for anything but diagnostics.</summary>
    public int Version { get; set; } = 1;

    // ---- Screenshot ingest -------------------------------------------------

    public string ScreenshotFolder { get; set; } = DefaultScreenshotFolder();

    public CullMode CullMode { get; set; } = CullMode.Off;

    /// <summary>Only meaningful when <see cref="CullMode"/> is <see cref="CullMode.KeepLatest"/>.</summary>
    public int CullKeepCount { get; set; } = 20;

    /// <summary>
    /// Send culled files to the Recycle Bin rather than unlinking them. Kept as a setting so the
    /// destructive path is visible in the config file, but there is no UI to turn it off.
    /// </summary>
    public bool CullToRecycleBin { get; set; } = true;

    // ---- Map ---------------------------------------------------------------

    public string CurrentMap { get; set; } = "customs";

    /// <summary>Suggest a map when a fix lands outside the current map's bounds.</summary>
    public bool SuggestMapFromPosition { get; set; } = true;

    /// <summary>Act on that suggestion without asking. Off by default: guesses are ambiguous.</summary>
    public bool AutoSwitchMap { get; set; }

    public double DefaultZoom { get; set; } = 1.0;
    public double MinZoom { get; set; } = 0.25;
    public double MaxZoom { get; set; } = 24.0;

    /// <summary>Recenter on the player each time a new fix arrives.</summary>
    public bool FollowPlayer { get; set; } = true;

    /// <summary>
    /// Ease the camera to its destination rather than jumping, for moves the app makes on the
    /// user's behalf: following the player, and framing an exit.
    /// </summary>
    /// <remarks>
    /// Never applies to the user's own panning and zooming, which have to stay locked to the
    /// pointer. A jump costs you your bearings -- you have to find yourself on the map again every
    /// screenshot -- where a move you can follow does not.
    /// </remarks>
    public bool SmoothCameraMovement { get; set; } = true;

    // ---- Route markers -----------------------------------------------------

    /// <summary>How close, in meters, counts as reaching a marker.</summary>
    public double WaypointArrivalRadiusMeters { get; set; } = 50.0;

    /// <summary>What happens to a marker once it is reached.</summary>
    public WaypointArrival WaypointArrival { get; set; } = WaypointArrival.MarkThenRemove;

    // ---- Extract focus -----------------------------------------------------

    public string? SelectedExtractId { get; set; }

    /// <summary>Frame player + selected extract instead of honoring the manual zoom.</summary>
    public bool ExtractFocusMode { get; set; }

    /// <summary>Extra margin around the player/extract rect, as a fraction of its size.</summary>
    public double ExtractFocusPadding { get; set; } = 0.15;

    /// <summary>Which exits are worth showing for the run you are on.</summary>
    public ExitFilter ExitFilter { get; set; } = ExitFilter.All;

    /// <summary>
    /// Order the exit list by distance from the player rather than by faction and name.
    /// Only has an effect once a screenshot has placed you.
    /// </summary>
    public bool SortExitsByDistance { get; set; }

    /// <summary>Whether the map's ground level is drawn. Off reveals an underground floor.</summary>
    public bool ShowBaseLayer { get; set; } = true;

    // ---- Reading exits off the screenshot ----------------------------------

    /// <summary>
    /// Look for Tarkov's extraction panel in each screenshot and dim the exits it does not list.
    /// </summary>
    /// <remarks>
    /// Off by default. It costs about 25 ms per screenshot and does nothing at all unless the
    /// player happens to have the panel open, so it should be a deliberate choice rather than a
    /// surprise. Nothing about it touches the game: the panel is already on the player's screen,
    /// and the screenshot is a file the game wrote itself.
    /// </remarks>
    public bool ReadExitsFromScreenshots { get; set; }

    // ---- Layers ------------------------------------------------------------

    public bool ShowHeatmap { get; set; }
    public double HeatmapRadiusMeters { get; set; } = 40.0;
    public double HeatmapOpacity { get; set; } = 0.55;

    /// <summary>
    /// Which spawn populations feed the heatmap, keyed by <c>SpawnGroup</c> name.
    /// </summary>
    /// <remarks>
    /// These keys must match the enum exactly. They were originally guessed from the GraphQL
    /// schema ("Player", "Sniper") before the real data showed the categories are
    /// player/bot/botpmc/boss, and the mismatch meant half the lookups silently missed and fell
    /// back to whatever the overlay happened to default to.
    /// </remarks>
    public Dictionary<string, bool> HeatmapCategories { get; set; } = new()
    {
        ["Pmc"] = true,
        ["Scav"] = true,
        ["AiPmc"] = false,
        ["Boss"] = false,
    };

    /// <summary>POI layer visibility, keyed by <c>PoiKind</c> name.</summary>
    public Dictionary<string, bool> PoiLayers { get; set; } = new()
    {
        ["ExtractPmc"] = true,
        ["ExtractScav"] = true,
        ["ExtractShared"] = true,
        ["Transit"] = true,
        ["Spawn"] = false,
        ["BossZone"] = false,
        ["LootContainer"] = false,
        ["Hazard"] = false,
        ["Lock"] = false,
        ["Switch"] = false,
        ["StationaryWeapon"] = false,
        ["BtrStop"] = false,
    };

    /// <summary>Number of past fixes drawn as a fading trail. 0 disables the trail.</summary>
    public int HistoryTrailLength { get; set; } = 12;

    // ---- Appearance --------------------------------------------------------

    /// <summary>
    /// The name shown to the rest of the squad when sharing positions. Local only; it is never
    /// checked against anything and there are no accounts.
    /// </summary>
    public string PlayerName { get; set; } = "";

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>Base UI font size in device-independent pixels. 14 is the accessible floor we target.</summary>
    public double FontSize { get; set; } = 14.0;

    public bool AlwaysOnTop { get; set; }

    public WindowPlacement? Window { get; set; }

    // ---- Data --------------------------------------------------------------

    /// <summary>How stale the cached tarkov.dev payload may get before we refetch.</summary>
    public int DataRefreshIntervalHours { get; set; } = 72;

    /// <summary>Allow outbound requests to tarkov.dev at all.</summary>
    public bool AllowNetwork { get; set; } = true;

    public static string DefaultScreenshotFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documents, "Escape from Tarkov", "Screenshots");
    }

    /// <summary>
    /// Clamps anything a hand-edited settings file could get wrong into a usable range, so a bad
    /// value degrades the UI instead of throwing somewhere deep in the render loop.
    /// </summary>
    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(ScreenshotFolder))
            ScreenshotFolder = DefaultScreenshotFolder();

        CullKeepCount = Math.Clamp(CullKeepCount, 1, 10_000);

        MinZoom = Math.Clamp(MinZoom, 0.01, 8.0);
        MaxZoom = Math.Clamp(MaxZoom, MinZoom * 2.0, 512.0);
        DefaultZoom = Math.Clamp(DefaultZoom, MinZoom, MaxZoom);

        ExtractFocusPadding = Math.Clamp(ExtractFocusPadding, 0.0, 2.0);

        // A radius under a few meters could never trigger from screenshots taken seconds apart,
        // and one in the hundreds would retire the whole route from the spawn.
        WaypointArrivalRadiusMeters = Math.Clamp(WaypointArrivalRadiusMeters, 5.0, 500.0);
        HeatmapRadiusMeters = Math.Clamp(HeatmapRadiusMeters, 1.0, 500.0);
        HeatmapOpacity = Math.Clamp(HeatmapOpacity, 0.0, 1.0);
        HistoryTrailLength = Math.Clamp(HistoryTrailLength, 0, 500);
        FontSize = Math.Clamp(FontSize, 10.0, 32.0);
        DataRefreshIntervalHours = Math.Clamp(DataRefreshIntervalHours, 1, 24 * 365);

        if (string.IsNullOrWhiteSpace(CurrentMap))
            CurrentMap = "customs";
    }
}

public sealed class WindowPlacement
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 860;
    public bool Maximized { get; set; }
}

/// <summary>
/// Source-generated serialization context. Keeps startup fast and keeps the settings round-trip
/// working if the app is ever published trimmed.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
