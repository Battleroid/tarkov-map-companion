using TarkovMapCompanion.Data;
using TarkovMapCompanion.Diagnostics;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Party;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Screenshots;
using TarkovMapCompanion.Settings;
using TarkovMapCompanion.Vision;

namespace TarkovMapCompanion;

/// <summary>
/// Ties the moving parts together: which map is shown, where the player is, and what happens to
/// screenshots after they are read.
/// </summary>
/// <remarks>
/// Kept separate from the window so the orchestration is testable and the view stays thin.
/// Events are raised on whatever thread the watcher used; the window marshals to the UI thread.
/// </remarks>
public sealed class MapSession : IDisposable
{
    private readonly AppSettings _settings;
    private readonly MapCatalog _catalog;
    private readonly AssetCache _assets;
    private readonly ScreenshotWatcher _watcher;
    private readonly ScreenshotCuller _culler;

    private readonly MapDataStore _mapData;
    private readonly ExtractNotesStore _extractNotes;

    private IMapImageSource? _imageSource;
    private int _fixes;
    private bool _disposed;

    private readonly object _readerGate = new();
    private IScreenTextReader? _textReader;

    /// <summary>
    /// Bumped whenever a reading stops being applicable: a new raid, or a different map. An OCR
    /// pass that finishes after its epoch has passed is discarded rather than applied late.
    /// </summary>
    private int _readingEpoch;

    public MapSession(AppSettings settings, MapCatalog catalog)
    {
        _settings = settings;
        _catalog = catalog;

        _assets = new AssetCache { AllowNetwork = settings.AllowNetwork };
        _culler = new ScreenshotCuller(settings);

        _mapData = new MapDataStore(settings);
        _mapData.Updated += (_, _) => RebuildPois();

        _extractNotes = new ExtractNotesStore();
        _extractNotes.Load();

        _watcher = new ScreenshotWatcher();
        _watcher.FixDetected += OnFixDetected;
        _watcher.Error += (_, message) => Status?.Invoke(this, message);

        Waypoints = new WaypointOverlay
        {
            ArrivalRadiusMeters = settings.WaypointArrivalRadiusMeters,
            Arrival = settings.WaypointArrival,
        };

        Peers = new PeerOverlay();
        Pings = new PingOverlay();
        Party = new PartySession();

        Party.Changed += (_, _) => Peers.SetPeers(Party.Peers);
        Party.Status += (_, message) => Status?.Invoke(this, message);
        Party.PingReceived += (_, ping) => OnPingReceived(ping);

        Player = new PlayerOverlay { TrailLength = settings.HistoryTrailLength };

        // Which exits a raid offers is decided when that raid starts, so a list read in the last
        // one says nothing about this one.
        Player.RaidStarted += (_, _) => ClearExitAvailability();

        Pois = new PoiOverlay();
        ExtractLine = new ExtractLineOverlay();

        Heatmap = new HeatmapOverlay
        {
            IsVisible = settings.ShowHeatmap,
            RadiusMeters = settings.HeatmapRadiusMeters,
            Opacity = settings.HeatmapOpacity,
        };

        foreach (var group in Enum.GetValues<SpawnGroup>())
        {
            if (settings.HeatmapCategories.TryGetValue(group.ToString(), out var on))
                Heatmap.Groups[group] = on;
        }

        foreach (var kind in Enum.GetValues<PoiKind>())
            Pois.Visible[kind] = settings.PoiLayers.TryGetValue(kind.ToString(), out var on) && on;

        CurrentMap = catalog.Resolve(settings.CurrentMap);
    }

    public GameMap CurrentMap { get; private set; }

    public PlayerOverlay Player { get; }

    public PoiOverlay Pois { get; }

    public ExtractLineOverlay ExtractLine { get; }

    /// <summary>The ordered route the player has drawn, if any.</summary>
    public WaypointOverlay Waypoints { get; }

    /// <summary>Position sharing with a squad. Idle unless the user starts or joins a session.</summary>
    public PartySession Party { get; }

    /// <summary>The rest of the squad, drawn on the map.</summary>
    public PeerOverlay Peers { get; }

    /// <summary>Short-lived "look here" marks, from the squad or from yourself.</summary>
    public PingOverlay Pings { get; }

    /// <summary>Raised when a ping lands, so the window can make a noise and start repainting.</summary>
    public event EventHandler<MapPing>? PingAdded;

    /// <summary>Drops a ping at a point on the map, and shares it if a session is running.</summary>
    public void SendPing(MapPoint basePoint)
    {
        var (x, z) = CurrentMap.Projection.ToGame(basePoint);
        Party.SendPing(CurrentMap.NormalizedName, new GamePosition(x, 0, z));
    }

    /// <summary>Raised when the route changes, whether by the user or by reaching a marker.</summary>
    public event EventHandler? WaypointsChanged;

    /// <summary>
    /// Points the guide line at the next marker, or back at the chosen exit once the route is
    /// done. Call after anything that could change either.
    /// </summary>
    public void RefreshGuideTarget() => ExtractLine.Waypoint = Waypoints.Next;

    public void AddWaypoint(MapPoint basePoint)
    {
        var (x, z) = CurrentMap.Projection.ToGame(basePoint);

        // Height is not recoverable from a map click, and nothing needs it: arrival is judged on
        // ground distance, the same as every other distance the app reports.
        Waypoints.Add(new GamePosition(x, 0, z), basePoint);

        RefreshGuideTarget();
        WaypointsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearWaypoints()
    {
        Waypoints.Clear();
        RefreshGuideTarget();
        WaypointsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveLastWaypoint()
    {
        if (!Waypoints.RemoveLast())
            return;

        RefreshGuideTarget();
        WaypointsChanged?.Invoke(this, EventArgs.Empty);
    }

    public HeatmapOverlay Heatmap { get; }

    public MapDataStore MapData => _mapData;

    public ExtractNotesStore ExtractNotes => _extractNotes;

    /// <summary>
    /// The extract the player is heading for. Drives the guide line and, when extract-focus mode
    /// is on, the framing.
    /// </summary>
    public MapPoi? SelectedExtract
    {
        get => Pois.Selected;
        set
        {
            Pois.Selected = value;
            ExtractLine.Target = value;
            _settings.SelectedExtractId = value?.Id;

            SelectionChanged?.Invoke(this, value);
        }
    }

    /// <summary>Raised when the selected extract changes, for the detail panel.</summary>
    public event EventHandler<MapPoi?>? SelectionChanged;

    public IMapImageSource? ImageSource => _imageSource;

    /// <summary>Raised after a new fix has been folded into <see cref="Player"/>.</summary>
    public event EventHandler<PlayerFix>? FixApplied;

    /// <summary>Raised when the shown map changes, for whatever reason.</summary>
    public event EventHandler<GameMap>? MapChanged;

    /// <summary>Human-readable progress and problems, for the status bar.</summary>
    public event EventHandler<string>? Status;

    /// <summary>
    /// Raised when a fix lands outside the current map but inside exactly one other, so the UI
    /// can offer a switch. Not raised when the guess is ambiguous.
    /// </summary>
    public event EventHandler<GameMap>? MapSuggested;

    /// <summary>Raised when the POI set for the current map has been rebuilt.</summary>
    public event EventHandler? PoisChanged;

    /// <summary>
    /// The exits the game listed for this raid, read off a screenshot, or null when unknown.
    /// </summary>
    public ExitAvailability? ExitAvailability { get; private set; }

    /// <summary>Raised when the read exit list appears, changes, or is dropped.</summary>
    public event EventHandler<ExitAvailability?>? ExitAvailabilityChanged;

    /// <summary>Why exits cannot be read on this machine, or null when they can.</summary>
    public string? ExitReaderUnavailableReason => EnsureTextReader()?.UnavailableReason;

    /// <summary>
    /// Forgets the exit list read from a screenshot, and invalidates any read still in flight.
    /// </summary>
    public void ClearExitAvailability()
    {
        Interlocked.Increment(ref _readingEpoch);

        if (ExitAvailability is null)
            return;

        ExitAvailability = null;
        Pois.Availability = null;

        ExitAvailabilityChanged?.Invoke(this, null);
    }

    private IScreenTextReader? EnsureTextReader()
    {
        lock (_readerGate)
            return _textReader ??= new WindowsOcrTextReader();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Local data first so the map is usable immediately; the network refresh catches up later.
        _mapData.LoadLocal();

        await SetMapAsync(CurrentMap, cancellationToken).ConfigureAwait(false);
        StartWatching();

        _ = RefreshDataInBackgroundAsync(cancellationToken);
    }

    private async Task RefreshDataInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _mapData.RefreshIfStaleAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Status?.Invoke(this, $"Map data refresh failed: {ex.Message}");
        }
    }

    /// <summary>Rebuilds POIs for the current map from whatever data is loaded.</summary>
    private void RebuildPois()
    {
        var data = _mapData.ForMap(CurrentMap.NormalizedName);

        Pois.Map = CurrentMap;
        Pois.SetPois(data is null ? [] : PoiBuilder.Build(CurrentMap, data, _mapData, _extractNotes));

        ExtractLine.Map = CurrentMap;
        ExtractLine.Target = null;

        Heatmap.Map = CurrentMap;
        Heatmap.SetData(data);

        // Bound the trail by how long a raid on this map can actually run, rather than the
        // conservative default. Factory is 20 minutes; Terminal is 50.
        if (_mapData.RaidDurationMinutes(CurrentMap.NormalizedName) is { } minutes and > 0)
            Player.MaxRaidLength = TimeSpan.FromMinutes(minutes + 5);

        RestoreSelectedExtract();
        PoisChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RestoreSelectedExtract()
    {
        if (string.IsNullOrEmpty(_settings.SelectedExtractId))
            return;

        var match = Pois.Extracts.FirstOrDefault(p =>
            string.Equals(p.Id, _settings.SelectedExtractId, StringComparison.Ordinal));

        // Assign through the field, not the property: the saved id belongs to whichever map it
        // was chosen on, and clearing it here would lose it when switching maps and back.
        Pois.Selected = match;
        ExtractLine.Target = match;
    }

    public void StartWatching()
    {
        var folder = _settings.ScreenshotFolder;
        _watcher.Start(folder);

        // Logged rather than only shown in the status bar, and with the count, because "is this
        // app even looking at the right folder" is the question behind almost every report of the
        // map not moving -- and until now the log said nothing about it either way.
        if (!Directory.Exists(folder))
        {
            Log.Warn($"[screenshots] folder does not exist: {folder}");
            Status?.Invoke(this, $"Folder not found: {folder}");
            return;
        }

        var count = Screenshots.ScreenshotFolders.CountIn(folder);

        // Screenshots taken outside a raid carry no coordinates at all -- Tarkov writes just a
        // date, a time and a frozen clock -- so they can never move the map. Counting them
        // separately turns "I took loads and nothing happened" into an answer rather than a
        // mystery, because the usual cause is pressing the key in the menu to test.
        var placeless = Directory.EnumerateFiles(folder, "*.png", SearchOption.TopDirectoryOnly)
            .Count(p => !Screenshots.ScreenshotNameParser.IsScreenshotFileName(p));

        if (placeless > 0)
        {
            Log.Info(
                $"[screenshots] {placeless} file(s) in the folder have no position in the name; "
                + "Tarkov writes those when you are not in a raid, and they are ignored");
        }

        if (count == 0)
        {
            Log.Warn(
                $"[screenshots] watching {folder}, which has no Tarkov screenshots in it. "
                + "If the game has written some, it is writing somewhere else -- try Find in Settings.");
        }
        else
        {
            Log.Info($"[screenshots] watching {folder} ({count} already there)");
        }

        Status?.Invoke(this, $"Watching {folder}");
    }

    /// <summary>
    /// Switches to a map, rebuilding its imagery and points of interest.
    /// </summary>
    /// <remarks>
    /// Idempotent: re-selecting the map already shown does nothing. Reloading it would reset the
    /// floor selection and drop the player's trail, and the caller cannot always tell whether a
    /// selection event reflects a real change -- a ComboBox can raise SelectionChanged while its
    /// template is reapplied, which is enough to silently undo whichever floors were switched on.
    /// </remarks>
    public async Task SetMapAsync(GameMap map, CancellationToken cancellationToken = default)
    {
        if (ReferenceEquals(map, CurrentMap) && _imageSource is { IsReady: true })
            return;

        CurrentMap = map;
        _settings.CurrentMap = map.NormalizedName;

        var previous = _imageSource;

        // SVG wherever it exists: one file covers every zoom level and carries the floor groups.
        // The three tile-only maps fall through to the tile renderer.
        _imageSource = map.HasSvg
            ? new SvgMapSource(map, _assets)
            : new TileMapSource(map, _assets);

        previous?.Dispose();

        // A trail from the previous map is meaningless here, and the clock heuristic cannot catch
        // this case: two raids on different maps an hour apart can have perfectly consistent
        // in-raid clocks, so the map change itself is the signal.
        Player.Clear();
        ClearExitAvailability();

        // A route is a set of points in this map's coordinates. Carried across, its pins would
        // land somewhere arbitrary on the new one.
        Waypoints.Map = map;
        ClearWaypoints();

        // Peers stay in the roster across a map change; the overlay simply stops drawing the ones
        // who are somewhere else.
        Peers.Map = map;

        // Pings are points in the old map's coordinates and are short-lived by nature; carrying
        // them across would put marks somewhere arbitrary.
        Pings.Map = map;
        Pings.Clear();

        Player.Map = map;
        RebuildPois();

        MapChanged?.Invoke(this, map);

        await _imageSource.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (!_imageSource.IsReady)
        {
            Status?.Invoke(this, _settings.AllowNetwork
                ? $"Could not download the {map.DisplayName} map. Check your connection."
                : $"{map.DisplayName} is not cached and network access is off.");
        }
    }

    private void OnFixDetected(object? sender, PlayerFix fix)
    {
        // This runs on the folder-watcher thread, where an unhandled exception takes the whole
        // process down without a word. One screenshot the app cannot make sense of must not close
        // the app in the middle of a raid, which is exactly when it is needed.
        try
        {
            // The first one proves the whole ingest path works; after that a count is enough. A
            // log with party lines but no screenshot lines used to be indistinguishable from one
            // where the app was reading fine and simply not sharing.
            if (++_fixes % 10 == 1)
                Log.Info($"[screenshots] read {_fixes} so far, latest {fix.FileName} on {CurrentMap.NormalizedName}");

            if (!CurrentMap.ContainsPosition(fix.Position))
                SuggestMapFor(fix);

            Player.Add(fix);

            ExtractLine.PlayerPosition = fix.Position;
            ExtractLine.PlayerYawDegrees = fix.YawDegrees;

            // Before FixApplied, so the window draws and measures against the route as it stands
            // after this fix rather than one update behind it.
            if (Waypoints.ApplyFix(fix.Position))
            {
                RefreshGuideTarget();
                WaypointsChanged?.Invoke(this, EventArgs.Empty);
            }

            // Only ever our own position, only when a session is running, and only what the
            // screenshot already told us.
            Party.Publish(CurrentMap.NormalizedName, fix.Position, fix.YawDegrees);

            FixApplied?.Invoke(this, fix);

            // Take a copy of the pixels before culling gets a chance to recycle the file. Reading
            // exits is slow enough to want off this thread, and DeleteAfterRead is fast enough to
            // beat it there.
            var image = ShouldReadExits() ? TryReadImage(fix.FilePath) : null;

            CullAfter(fix);

            if (image is not null)
                _ = ReadExitsAsync(image, fix, CurrentMap, Volatile.Read(ref _readingEpoch));
        }
        catch (Exception ex)
        {
            Log.Error($"failed to apply fix from {fix.FileName}", ex);
            Status?.Invoke(this, $"Could not read {fix.FileName}; see {Log.Path}");
        }
    }

    /// <summary>
    /// Offers a different map when the fix cannot be on this one. Deliberately silent when more
    /// than one map matches: several maps overlap in world coordinates, and a wrong auto-switch
    /// mid-raid is worse than no suggestion.
    /// </summary>
    private void SuggestMapFor(PlayerFix fix)
    {
        if (!_settings.SuggestMapFromPosition)
            return;

        var candidates = _catalog.Maps.Where(m => m.ContainsPosition(fix.Position)).ToArray();
        if (candidates.Length != 1)
            return;

        MapSuggested?.Invoke(this, candidates[0]);
    }

    private void CullAfter(PlayerFix fix)
    {
        if (_settings.CullMode == CullMode.Off)
            return;

        var results = _culler.Apply(_settings.ScreenshotFolder, fix.FilePath);

        foreach (var result in results)
        {
            if (result.Deleted)
            {
                // Let the watcher report it again if the same name reappears.
                _watcher.Forget(result.Path);
            }
            else if (result.Refusal is not (CullRefusal.None or CullRefusal.WithinKeepWindow))
            {
                Status?.Invoke(this, $"Kept {Path.GetFileName(result.Path)}: {Describe(result.Refusal)}");
            }
        }

        var deleted = results.Count(r => r.Deleted);
        if (deleted > 0)
            Status?.Invoke(this, $"Removed {deleted} old screenshot{(deleted == 1 ? "" : "s")} to the Recycle Bin");
    }

    /// <summary>
    /// Turns an incoming ping into something drawable, colored to match whoever sent it.
    /// </summary>
    /// <remarks>
    /// Runs on a socket thread for a peer's ping and on the UI thread for our own; the overlay is
    /// locked and the window marshals the event, so both are fine.
    /// </remarks>
    private void OnPingReceived(PeerPosition ping)
    {
        var mine = string.Equals(ping.Name, Party.SelfName, StringComparison.OrdinalIgnoreCase);

        var placed = new MapPing
        {
            Name = ping.Name,
            Map = ping.Map,
            Position = new GamePosition(ping.X, ping.Y, ping.Z),
            Color = mine ? Rendering.MarkerPalette.Player : Peers.ColorFor(ping.Name),
        };

        Pings.Add(placed);
        PingAdded?.Invoke(this, placed);
    }

    private bool ShouldReadExits() =>
        _settings.ReadExitsFromScreenshots && EnsureTextReader() is { IsAvailable: true };

    /// <summary>
    /// Loads a screenshot, waiting for the game to finish writing it.
    /// </summary>
    /// <remarks>
    /// The folder watcher fires as soon as the file appears, and Tarkov's screenshots are several
    /// megabytes, so the first look at one very often catches it half written. The filename is
    /// complete from the start -- which is why positions never needed this -- but the pixels are
    /// not, and a truncated PNG decodes to nothing useful. A complete PNG ends with its IEND chunk,
    /// which makes "is this finished" a cheap and exact question rather than a guess at a delay.
    /// </remarks>
    internal static byte[]? TryReadImage(string path)
    {
        ReadOnlySpan<byte> pngEnd = [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];

        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(80);

            try
            {
                // Share write: the game may still have the file open.
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);

                if (bytes.Length > 16 && bytes.AsSpan(bytes.Length - 8).SequenceEqual(pngEnd))
                    return bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked or gone; either way it is worth one more look.
            }
        }

        Log.Warn($"gave up waiting for {Path.GetFileName(path)} to finish writing");
        return null;
    }

    /// <summary>
    /// Reads Tarkov's extraction panel out of a screenshot and narrows the exit list to it.
    /// </summary>
    /// <remarks>
    /// Nothing here touches the game. The panel is drawn by the game on the player's own screen,
    /// and the image is a file the game wrote; this only saves them from memorizing eight names.
    /// </remarks>
    private async Task ReadExitsAsync(byte[] image, PlayerFix fix, GameMap map, int epoch)
    {
        try
        {
            if (EnsureTextReader() is not { IsAvailable: true } reader)
                return;

            var lines = await reader.ReadAsync(image, RelativeRegion.ExtractPanel).ConfigureAwait(false);
            var reading = ExtractPanelParser.Parse(lines);

            // Most screenshots are just screenshots. Saying nothing is the right answer, and in
            // particular is not the same as saying the player has no exits.
            if (!reading.PanelFound)
                return;

            var exits = Pois.Extracts.ToArray();
            var availability = ExitAvailability.Resolve(reading, exits, map.NormalizedName, fix.TakenAt);

            if (availability is null)
            {
                Log.Warn($"exit panel read from {fix.FileName} matched no known exit on {map.NormalizedName}");
                Status?.Invoke(this, "Found the exit panel but could not match any names; leaving all exits shown.");
                return;
            }

            // The raid or the map may have moved on while this was decoding.
            if (Volatile.Read(ref _readingEpoch) != epoch)
                return;

            // Two screenshots in one raid are two looks at the same list, so combine them. A
            // screenshot that caught the panel opening sees only part of it, and on its own would
            // dim exits an earlier, fuller look had already found.
            availability = availability.MergedWith(ExitAvailability);

            ExitAvailability = availability;
            Pois.Availability = availability;

            Log.Info(
                $"read {availability.NameCount} exits from {fix.FileName} on {map.NormalizedName}"
                + (availability.Unresolved.Count > 0
                    ? $"; unresolved: {string.Join(" | ", availability.Unresolved)}"
                    : ""));

            ExitAvailabilityChanged?.Invoke(this, availability);
        }
        catch (Exception ex)
        {
            Log.Error($"failed to read exits from {fix.FileName}", ex);
        }
    }

    private static string Describe(CullRefusal refusal) => refusal switch
    {
        CullRefusal.OutsideWatchedFolder => "outside the watched folder",
        CullRefusal.NotAScreenshotName => "not a Tarkov screenshot",
        CullRefusal.DeleteFailed => "could not be deleted",
        CullRefusal.Disabled => "culling is off",
        _ => "skipped",
    };

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _watcher.FixDetected -= OnFixDetected;
        _watcher.Dispose();
        Party.Dispose();
        _imageSource?.Dispose();
    }
}
