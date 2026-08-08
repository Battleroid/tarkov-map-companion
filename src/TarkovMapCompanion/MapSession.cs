using TarkovMapCompanion.Data;
using TarkovMapCompanion.Diagnostics;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Screenshots;
using TarkovMapCompanion.Settings;

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
    private bool _disposed;

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

        Player = new PlayerOverlay { TrailLength = settings.HistoryTrailLength };
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
        _watcher.Start(_settings.ScreenshotFolder);

        Status?.Invoke(this, Directory.Exists(_settings.ScreenshotFolder)
            ? $"Watching {_settings.ScreenshotFolder}"
            : $"Folder not found: {_settings.ScreenshotFolder}");
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
            if (!CurrentMap.ContainsPosition(fix.Position))
                SuggestMapFor(fix);

            Player.Add(fix);

            ExtractLine.PlayerPosition = fix.Position;
            ExtractLine.PlayerYawDegrees = fix.YawDegrees;

            FixApplied?.Invoke(this, fix);

            CullAfter(fix);
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
        _imageSource?.Dispose();
    }
}
