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

    // ---- Reading the game's own log ----------------------------------------

    /// <summary>
    /// Follow the log Escape from Tarkov writes, so the app knows which map is loading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default, and deliberately so. Every other input to this app is something the player
    /// made on purpose -- a screenshot they pressed a key for -- and this one is the game's own
    /// running commentary. It is still only a text file on disk that anyone can open in Notepad,
    /// nothing is written and nothing is injected, but it is a big enough difference in kind to be
    /// worth asking for rather than assuming.
    /// </para>
    /// <para>
    /// What it buys is the map being right before you have taken a single screenshot: the game
    /// names the map it is loading somewhere between twenty seconds and two minutes before you have
    /// control.
    /// </para>
    /// </remarks>
    public bool ReadGameLog { get; set; }

    /// <summary>
    /// Switch the map as soon as the log names it, instead of offering the switch.
    /// </summary>
    /// <remarks>
    /// On by default, unlike the equivalent for a position-based guess. The two are not comparable:
    /// a coordinate that could be one of several overlapping maps is a guess, and the game saying
    /// <c>Location: bigmap</c> is not.
    /// </remarks>
    public bool AutoSwitchMapFromGameLog { get; set; } = true;

    /// <summary>
    /// Where Tarkov's <c>Logs</c> folder is. Empty means look for it at every startup.
    /// </summary>
    /// <remarks>
    /// Empty rather than a detected path, so a machine whose install moves is not stuck pointing at
    /// where the game used to be. The override exists because the launcher will install anywhere
    /// and detection is genuinely best-effort.
    /// </remarks>
    public string GameLogFolder { get; set; } = "";

    /// <summary>
    /// How wide the exits/quests/notes panel is, in pixels. 0 means folded away.
    /// </summary>
    /// <remarks>
    /// A preference rather than a constant because the three tabs want different amounts of room:
    /// the exit list is comfortable narrow, and a quest row carries a wrapping task name followed
    /// by two buttons.
    /// </remarks>
    public double SidebarWidth { get; set; } = 290.0;

    /// <summary>How wide the quest detail pane is when open, in pixels.</summary>
    public double QuestPaneWidth { get; set; } = 360.0;

    // ---- Annotations -------------------------------------------------------

    /// <summary>Draw the text notes written on the map. On: they are not there unless asked for.</summary>
    public bool ShowAnnotations { get; set; } = true;

    /// <summary>
    /// Send your own map notes to the squad.
    /// </summary>
    /// <remarks>
    /// Off by default, unlike routes. A route is a plan for the next ten minutes; a set of notes is
    /// something somebody built up over weeks, and pushing that onto three other people's maps the
    /// moment they join is not a thing to do without being asked.
    /// </remarks>
    public bool ShareAnnotationsWithParty { get; set; }

    // ---- Quests ------------------------------------------------------------

    /// <summary>
    /// Ids of the tasks whose objectives are drawn on the map.
    /// </summary>
    /// <remarks>
    /// Ticked by hand, because there is no honest way to know which quests you have accepted: that
    /// lives in your profile on BSG's servers, and the app reads files rather than accounts. Ids
    /// rather than names, so a task being renamed upstream does not silently untick it.
    /// </remarks>
    public List<string> TrackedTasks { get; set; } = [];

    /// <summary>
    /// Your PMC level. Only ever used to filter the quest list.
    /// </summary>
    public int PlayerLevel { get; set; } = 1;

    /// <summary>Label quest markers with their task name, rather than only on hover.</summary>
    public bool ShowQuestNames { get; set; } = true;

    /// <summary>
    /// Let the game's own notification log decide which quests are being tracked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On when the log is being read at all, because hand-ticking sixty quests is the kind of
    /// chore nobody does twice. Trader messages carry the task id when a quest is accepted, handed
    /// in or failed, so the app can follow along without being told.
    /// </para>
    /// <para>
    /// It only knows what the logs kept. A quest accepted before the oldest surviving log looks
    /// untouched, which is why ticking by hand still works and still wins: a manual tick is never
    /// undone by the log saying nothing.
    /// </para>
    /// </remarks>
    public bool TrackQuestsFromGameLog { get; set; } = true;

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

    // ---- Markers -----------------------------------------------------------

    /// <summary>
    /// Your own marker color, as <c>#RRGGBB</c>. Also the color you appear as to your squad.
    /// </summary>
    /// <remarks>
    /// One setting, not two. Being one color on your own map and another on everyone else's is the
    /// kind of thing that only ever confuses people mid-raid.
    /// </remarks>
    public string PlayerColor { get; set; } = "#F5C942";

    /// <summary>Size of the player chevron in screen pixels, independent of zoom.</summary>
    public double PlayerMarkerSize { get; set; } = 22.0;

    /// <summary>Color of the line drawn to the selected exit, as <c>#RRGGBB</c>.</summary>
    public string GuideLineColor { get; set; } = "#F5C942";

    /// <summary>
    /// How many past positions to keep per teammate. 0 turns peer trails off.
    /// </summary>
    /// <remarks>
    /// Much shorter than your own trail on purpose. Teammates only report when they take a
    /// screenshot, so their history is sparse to begin with, and the useful signal is "which way has
    /// he been drifting" rather than a step-by-step record.
    /// </remarks>
    public int PeerTrailLength { get; set; } = 5;

    /// <summary>
    /// Point an arrow at teammates who are off the edge of the view.
    /// </summary>
    public bool ShowOffScreenPeers { get; set; } = true;

    /// <summary>
    /// March arrowheads along the route toward the next marker.
    /// </summary>
    /// <remarks>
    /// Turning it off still draws the arrowheads, it just stops them moving -- which is also what
    /// lets the shared render clock stop, so this is the knob for anyone who would rather the app
    /// did nothing at all while they are not looking at it.
    /// </remarks>
    public bool AnimateRouteArrows { get; set; } = true;

    /// <summary>
    /// Show an exit's conditions in the detail panel and the map tooltip.
    /// </summary>
    /// <remarks>
    /// Only the prose. The "!" in the list and the dashed ring on the map are not affected: folding
    /// away the clutter is decluttering, but hiding the fact that an exit has conditions at all
    /// would be a trap.
    /// </remarks>
    public bool ShowExitConditions { get; set; } = true;

    // ---- Appearance --------------------------------------------------------

    /// <summary>
    /// The name shown to the rest of the squad when sharing positions. Local only; it is never
    /// checked against anything and there are no accounts.
    /// </summary>
    public string PlayerName { get; set; } = "";

    /// <summary>
    /// The port hosting listens on.
    /// </summary>
    /// <remarks>
    /// Fixed rather than picked at random, because a manual port forward is pinned to one number.
    /// If it is taken, hosting refuses and says so instead of quietly moving somewhere the forward
    /// does not point.
    /// </remarks>
    public int PartyPort { get; set; } = Party.PartySession.DefaultPort;

    /// <summary>
    /// Make a noise when a ping lands. On by default: a ping you only notice by looking at the map
    /// is half a feature, since the point is to get your attention while you are in the game.
    /// </summary>
    public bool PingSound { get; set; } = true;

    /// <summary>
    /// Send your route markers to the rest of the squad.
    /// </summary>
    /// <remarks>
    /// On by default, but refusable: a squad that has already agreed to share positions has made
    /// the harder call, and yet a route is a plan rather than a fact about where you are.
    /// </remarks>
    public bool ShareRouteWithParty { get; set; } = true;

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>Base UI font size in device-independent pixels. 14 is the accessible floor we target.</summary>
    public double FontSize { get; set; } = 14.0;

    public bool AlwaysOnTop { get; set; }

    public WindowPlacement? Window { get; set; }

    // ---- Minimap -----------------------------------------------------------

    /// <summary>
    /// How solid the minimap window is. 1.0 is opaque.
    /// </summary>
    /// <remarks>
    /// Clamped well above invisible. A window you cannot see but which still eats your clicks is a
    /// state nobody would choose deliberately and everybody would reach by dragging a slider.
    /// </remarks>
    public double MinimapOpacity { get; set; } = 0.85;

    /// <summary>How much of the map the minimap shows, as a radius in game meters.</summary>
    public double MinimapRangeMeters { get; set; } = 150.0;

    /// <summary>
    /// Let clicks pass straight through the minimap to whatever is underneath.
    /// </summary>
    /// <remarks>
    /// Off by default, and deliberately not on the minimap itself: turning it on makes the window
    /// unclickable, so the control that turns it back off cannot live there.
    /// </remarks>
    public bool MinimapClickThrough { get; set; }

    public WindowPlacement? MinimapPlacement { get; set; }

    // ---- Data --------------------------------------------------------------

    /// <summary>How stale the cached tarkov.dev payload may get before we refetch.</summary>
    public int DataRefreshIntervalHours { get; set; } = 72;

    /// <summary>Allow outbound requests to tarkov.dev at all.</summary>
    public bool AllowNetwork { get; set; } = true;

    /// <summary>
    /// Where to watch, found by looking rather than assumed.
    /// </summary>
    /// <remarks>
    /// This used to be one hard-coded path under Documents, which is right until OneDrive has moved
    /// Documents somewhere else -- and then the app watches an empty folder forever and the map
    /// simply never moves, with nothing on screen to say why.
    /// </remarks>
    public static string DefaultScreenshotFolder() => Screenshots.ScreenshotFolders.Detect();

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

        // Above the well-known range, below the ephemeral range Windows hands out on its own.
        PartyPort = Math.Clamp(PartyPort, 1024, 49151);
        HeatmapRadiusMeters = Math.Clamp(HeatmapRadiusMeters, 1.0, 500.0);
        HeatmapOpacity = Math.Clamp(HeatmapOpacity, 0.0, 1.0);
        HistoryTrailLength = Math.Clamp(HistoryTrailLength, 0, 500);

        // Below about 10px the heading is unreadable; above about 48 the marker hides the ground
        // it is standing on, which is the one thing you were looking at.
        PlayerMarkerSize = Math.Clamp(PlayerMarkerSize, 10.0, 48.0);
        PeerTrailLength = Math.Clamp(PeerTrailLength, 0, 20);

        // Anything unparseable becomes the default rather than a transparent marker.
        PlayerColor = Rendering.ColorCodec.Canonical(PlayerColor, Rendering.MarkerPalette.Player);
        GuideLineColor = Rendering.ColorCodec.Canonical(GuideLineColor, Rendering.MarkerPalette.ExtractLine);

        // Never fully transparent. A window you cannot see but which still takes your clicks is a
        // state nobody picks on purpose and anybody reaches by dragging a slider to the end.
        MinimapOpacity = Math.Clamp(MinimapOpacity, 0.25, 1.0);
        MinimapRangeMeters = Math.Clamp(MinimapRangeMeters, 25.0, 1000.0);

        // The level cap at the time of writing. Only drives a filter, so being a patch behind
        // costs a tick box rather than anything real.
        PlayerLevel = Math.Clamp(PlayerLevel, 1, 79);

        // Zero is folded away and legitimate; anything between that and readable is not, so a
        // sliver left behind by a stray drag snaps back to something usable.
        if (SidebarWidth > 0)
            SidebarWidth = Math.Clamp(SidebarWidth, 220.0, 700.0);
        else
            SidebarWidth = 0;

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
