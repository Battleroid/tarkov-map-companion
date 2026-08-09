using TarkovMapCompanion.Data;
using TarkovMapCompanion.Diagnostics;
using TarkovMapCompanion.GameLog;
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
    private readonly GameLogWatcher _gameLog;

    /// <summary>
    /// Whether the log has told us a raid is running.
    /// </summary>
    /// <remarks>
    /// Only ever read by the log handler. It exists because Tarkov writes no "raid over" line, so
    /// the end has to be inferred from the profile reloading -- which also happens on the way in,
    /// and twice in a row on the way out.
    /// </remarks>
    private bool _inRaidPerLog;

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

        _gameLog = new GameLogWatcher();
        _gameLog.EventRead += OnGameLogEvent;
        _gameLog.Error += (_, message) => Log.Warn($"[game log] {message}");

        Waypoints = new WaypointOverlay
        {
            ArrivalRadiusMeters = settings.WaypointArrivalRadiusMeters,
            Arrival = settings.WaypointArrival,
            AnimateArrows = settings.AnimateRouteArrows,
        };

        Peers = new PeerOverlay
        {
            TrailLength = settings.PeerTrailLength,
            ShowOffScreen = settings.ShowOffScreenPeers,
        };

        Pings = new PingOverlay();

        // Set before any session starts, so it rides along on the Hello rather than needing an
        // announcement the moment anybody joins.
        Party = new PartySession { SelfColor = settings.PlayerColor };

        Party.RoutesChanged += (_, _) => RebuildSharedRoutes();

        Party.Changed += (_, _) =>
        {
            Peers.SetPeers(Party.Peers);

            // Colors are carried on the roster, so a change to one arrives here rather than with
            // the routes. Rebuild so a teammate's route follows their new color.
            RebuildSharedRoutes();

            // Ending a session has to take the trails with it. SetPeers prunes anyone missing from
            // the roster, and leaving empties the roster, so this is belt and braces -- but a stale
            // trail left drawn across the map after a session ends is exactly the sort of thing
            // that gets reported as "it is showing me someone who is not there".
            if (!Party.IsActive)
                Peers.ClearTrails();
        };
        Party.Status += (_, message) => Status?.Invoke(this, message);
        Party.PingReceived += (_, ping) => OnPingReceived(ping);

        Player = new PlayerOverlay
        {
            TrailLength = settings.HistoryTrailLength,
            MarkerSize = (float)settings.PlayerMarkerSize,
            Color = ColorCodec.Parse(settings.PlayerColor, MarkerPalette.Player),
        };

        // Which exits a raid offers is decided when that raid starts, so a list read in the last
        // one says nothing about this one.
        Player.RaidStarted += (_, _) => ClearExitAvailability();

        Pois = new PoiOverlay();
        ExtractLine = new ExtractLineOverlay
        {
            Color = ColorCodec.Parse(settings.GuideLineColor, MarkerPalette.ExtractLine),
        };

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
        if (!Waypoints.Add(new GamePosition(x, 0, z), basePoint))
        {
            Status?.Invoke(this, $"A route holds at most {WaypointOverlay.MaxWaypoints} markers.");
            return;
        }

        RefreshGuideTarget();
        PublishRoute();
        WaypointsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearWaypoints()
    {
        Waypoints.Clear();
        RefreshGuideTarget();
        PublishRoute();
        WaypointsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveLastWaypoint()
    {
        if (!Waypoints.RemoveLast())
            return;

        RefreshGuideTarget();
        PublishRoute();
        WaypointsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Resends the route, for when the sharing preference changes rather than the route.</summary>
    public void RepublishRoute() => PublishRoute();

    /// <summary>
    /// Builds a second, independent renderer for the current map.
    /// </summary>
    /// <remarks>
    /// The minimap cannot share the main window's source. SvgMapSource holds one rasterized
    /// snapshot keyed by zoom, so two canvases showing the same map at different scales would
    /// invalidate each other's snapshot and re-rasterize on every single frame. A second instance
    /// costs one more SVG parse and one more snapshot, which is a fair price for a window that is
    /// optional and closed by default.
    /// </remarks>
    public async Task<IMapImageSource> CreateImageSourceAsync(CancellationToken cancellationToken = default)
    {
        var map = CurrentMap;

        IMapImageSource source = map.HasSvg
            ? new SvgMapSource(map, _assets)
            : new TileMapSource(map, _assets);

        await source.LoadAsync(cancellationToken).ConfigureAwait(true);
        return source;
    }

    /// <summary>
    /// Projects the session's routes into the overlay, dropping our own.
    /// </summary>
    /// <remarks>
    /// Ours is already drawn from the local waypoint list, in full color and with the arrival ring.
    /// Drawing it twice would put a faint copy under the real one.
    /// </remarks>
    private void RebuildSharedRoutes()
    {
        var mine = Party.SelfName;

        var routes = Party.Routes
            .Where(r => !string.Equals(r.Name, mine, StringComparison.OrdinalIgnoreCase))
            .Select(r => new WaypointOverlay.SharedRoute(
                r.Name,
                r.Map,
                Peers.ColorFor(r.Name),
                r.Points.Select(p => new GamePosition(p.X, 0, p.Z)).ToArray()))
            .ToArray();

        Waypoints.SetSharedRoutes(routes);
    }

    /// <summary>
    /// Sends our route to the squad, or an empty one when there is nothing left to send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the points not yet reached. Under the default arrival mode a pin lingers for one extra
    /// fix as your own confirmation that you got there; a teammate seeing a ghost pin with a tick
    /// they cannot interpret is just noise. You get the confirmation, everyone else sees it go.
    /// </para>
    /// <para>
    /// The empty case still sends. Clearing your markers has to reach everybody, and treating
    /// "nothing to say" as "say nothing" would leave your route drawn on their maps until the
    /// session ended.
    /// </para>
    /// </remarks>
    private void PublishRoute()
    {
        // Opting out publishes an empty route rather than going quiet, so turning the setting off
        // withdraws what the squad is already drawing instead of freezing it there.
        if (!_settings.ShareRouteWithParty)
        {
            Party.PublishRoute(CurrentMap.NormalizedName, []);
            return;
        }

        var points = Waypoints.Waypoints
            .Where(w => !w.Visited)
            .Select(w => (w.Position.X, w.Position.Z))
            .ToArray();

        Party.PublishRoute(CurrentMap.NormalizedName, points);
    }

    public HeatmapOverlay Heatmap { get; }

    /// <summary>
    /// Everything drawn on top of the map, for a second canvas to render the same picture.
    /// </summary>
    /// <remarks>
    /// Shared instances, not copies. Every overlay already hands out a snapshot under a lock,
    /// because the folder-watcher thread writes them while the render thread reads -- so a second
    /// reader needs no synchronization and cannot drift out of step with the first.
    /// </remarks>
    public IReadOnlyList<IMapOverlay> Overlays =>
        [Heatmap, Pois, Waypoints, ExtractLine, Peers, Pings, Player];

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
    /// Raised when the game's own log names the map it is loading.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="MapSuggested"/> even though the window does something similar
    /// with both. That one is a guess from a coordinate that several maps could contain; this one
    /// is the game saying which map it is. They deserve different wording and different defaults.
    /// </remarks>
    public event EventHandler<GameMap>? MapDetectedFromLog;

    /// <summary>Raised when the log says a raid started (true) or the player is back in the menu.</summary>
    public event EventHandler<bool>? RaidStateChanged;

    /// <summary>Following the game's log, for the preferences screen.</summary>
    public GameLogWatcher GameLog => _gameLog;

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
        StartWatchingGameLog();

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
    /// Starts, restarts, or stops following the game's log, according to the current settings.
    /// </summary>
    /// <remarks>
    /// Called at startup and again whenever the preference or the folder changes, so the setting
    /// takes effect without a restart. Turning it off really does stop reading the file.
    /// </remarks>
    public void StartWatchingGameLog()
    {
        _gameLog.Stop();

        if (!_settings.ReadGameLog)
        {
            Log.Info("[game log] not reading the game log; the setting is off");
            return;
        }

        var folder = string.IsNullOrWhiteSpace(_settings.GameLogFolder)
            ? GameLogFolders.Detect()
            : _settings.GameLogFolder;

        if (folder is null)
        {
            Log.Warn("[game log] could not find where Tarkov is installed; run --find-logs to see what was tried");
            Status?.Invoke(this, "Could not find Tarkov's log folder. Set it in Settings.");
            return;
        }

        _gameLog.Start(folder);

        var launches = GameLogFolders.CountLogFolders(folder);
        Log.Info($"[game log] following {folder} ({launches} launches recorded)");

        if (launches == 0)
            Status?.Invoke(this, $"No Tarkov logs in {folder} yet.");
    }

    /// <summary>
    /// Acts on a line from Tarkov's own log.
    /// </summary>
    /// <remarks>
    /// Runs on the log watcher's thread, where an escaping exception takes the process with it, so
    /// everything is contained here in the same way the screenshot path is.
    /// </remarks>
    private void OnGameLogEvent(object? sender, GameLogEvent entry)
    {
        try
        {
            switch (entry.Kind)
            {
                case GameLogEventKind.ScenePreset:
                case GameLogEventKind.RaidCreated:
                    AnnounceMapFrom(entry);
                    break;

                case GameLogEventKind.RaidStarted:
                    _inRaidPerLog = true;

                    // The same three things a map change clears, for the same reason: none of it
                    // describes the raid that is starting. This is strictly better than the clock
                    // heuristic that would otherwise work it out a screenshot or two later.
                    Player.Clear();
                    ClearExitAvailability();
                    Pings.Clear();

                    Log.Info("[game log] raid started");
                    RaidStateChanged?.Invoke(this, true);
                    break;

                case GameLogEventKind.MenuReturned:
                    // Fires on the way into a session as well as out of one, and twice in a row on
                    // the way out, so it only means anything while a raid is known to be running.
                    if (!_inRaidPerLog)
                        return;

                    _inRaidPerLog = false;

                    // Nothing is cleared. The map after a raid is the one thing people actually
                    // look at afterward -- where the fight was, which exit they took -- and the
                    // next raid clears it anyway.
                    Log.Info("[game log] back at the menu");
                    RaidStateChanged?.Invoke(this, false);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"failed to act on a game log line: {entry.Line}", ex);
        }
    }

    /// <summary>
    /// Works out which map a log line is talking about, and says so.
    /// </summary>
    /// <remarks>
    /// Silent about a name it cannot place, apart from the log. Switching to the wrong map mid-raid
    /// is worse than not switching, and an unrecognized location id means the app is out of date
    /// rather than that the player is somewhere strange.
    /// </remarks>
    private void AnnounceMapFrom(GameLogEvent entry)
    {
        var map = entry.MapTokens
            .Select(token => _catalog.ResolveByNameId(token)
                             ?? _catalog.Find(_mapData.NormalizedNameForNameId(token)))
            .FirstOrDefault(m => m is not null);

        if (map is null)
        {
            Log.Warn(
                $"[game log] {entry.Kind} named a map this build does not know: "
                + $"{string.Join(" | ", entry.MapTokens)}");
            return;
        }

        if (ReferenceEquals(map, CurrentMap))
            return;

        Log.Info($"[game log] {entry.Kind} says {map.NormalizedName}");
        MapDetectedFromLog?.Invoke(this, map);
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

            // Off-screen teammate arrows are labeled with how far away they are, which needs a
            // position to measure from.
            Peers.PlayerPosition = fix.Position;

            // Before FixApplied, so the window draws and measures against the route as it stands
            // after this fix rather than one update behind it.
            if (Waypoints.ApplyFix(fix.Position))
            {
                RefreshGuideTarget();

                // Reaching your own marker is what retires it for everybody. Nobody else's arrival
                // touches it, so there is no agreement to reach and no radius to guess at.
                PublishRoute();
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
            Color = mine ? Player.Color : Peers.ColorFor(ping.Name),
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

        _gameLog.EventRead -= OnGameLogEvent;
        _gameLog.Dispose();
        Party.Dispose();
        _imageSource?.Dispose();
    }
}
