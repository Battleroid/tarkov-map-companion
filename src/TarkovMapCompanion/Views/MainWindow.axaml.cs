using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Screenshots;
using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MapCatalog _catalog;
    private readonly MapSession _session;
    private readonly MapCanvas _canvas = new();

    private readonly Dictionary<PoiKind, CheckBox> _layerToggles = [];

    private GameMap? _suggestedMap;
    private bool _suppressMapSelectorEvent;
    private bool _suppressExtractSelectionEvent;
    private bool _loadingControls = true;

    /// <summary>Where the view was before extract-focus mode took it over.</summary>
    private Viewport.State? _viewBeforeFocus;

    // Parameterless ctor exists only for the XAML previewer.
    public MainWindow() : this(new AppSettings())
    {
    }

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        _catalog = MapCatalog.LoadEmbedded();
        _session = new MapSession(settings, _catalog);

        InitializeComponent();

        FontSize = _settings.FontSize;

        // The canvas letterbox color comes from the MapCanvas style in Theming/Controls.axaml,
        // so it follows the theme without any wiring here.
        MapHost.Children.Add(_canvas);

        BuildMapSelector();
        WireControls();
        WireSession();

        // Controls are populated; from here on, changes are the user's and get persisted.
        _loadingControls = false;

        // The saved filter has to reach the layers before the first render.
        ApplyExitFilter();

        Opened += OnOpened;
        Closing += (_, _) =>
        {
            // Belt and braces: the app also saves on shutdown, but map choice, selected exit and
            // layer toggles are changed constantly during a session and losing them to an
            // ungraceful exit would be annoying out of all proportion to the cost of saving here.
            PersistSettings();
            _session.Dispose();
        };
    }

    private void BuildMapSelector()
    {
        MapSelector.ItemsSource = _catalog.Maps;
        MapSelector.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(GameMap.DisplayName));

        _suppressMapSelectorEvent = true;
        MapSelector.SelectedItem = _session.CurrentMap;
        _suppressMapSelectorEvent = false;

        MapSelector.SelectionChanged += OnMapSelectionChanged;
    }

    private void WireControls()
    {
        AlwaysOnTopToggle.IsChecked = _settings.AlwaysOnTop;
        AlwaysOnTopToggle.IsCheckedChanged += (_, _) =>
        {
            _settings.AlwaysOnTop = AlwaysOnTopToggle.IsChecked ?? false;
            Topmost = _settings.AlwaysOnTop;
        };

        FollowToggle.IsChecked = _settings.FollowPlayer;
        FollowToggle.IsCheckedChanged += (_, _) =>
            _settings.FollowPlayer = FollowToggle.IsChecked ?? false;

        ThemeButton.Click += OnThemeClicked;
        FitButton.Click += (_, _) => _canvas.FitAll();

        PreferencesButton.Click += async (_, _) => await ShowPreferencesAsync();
        AboutButton.Click += async (_, _) => await new AboutWindow(_catalog).ShowDialog(this);

        FocusToggle.IsChecked = _settings.ExtractFocusMode;
        FocusToggle.IsCheckedChanged += (_, _) => OnFocusToggled();

        ExitFilterBox.ItemsSource = Enum.GetValues<ExitFilter>().Select(f => f.Label()).ToArray();
        ExitFilterBox.SelectedIndex = (int)_settings.ExitFilter;
        ExitFilterBox.SelectionChanged += (_, _) => Apply(() =>
        {
            _settings.ExitFilter = (ExitFilter)Math.Max(0, ExitFilterBox.SelectedIndex);
            ApplyExitFilter();
        });

        SortByDistanceToggle.IsChecked = _settings.SortExitsByDistance;
        SortByDistanceToggle.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _settings.SortExitsByDistance = SortByDistanceToggle.IsChecked ?? false;
            RebuildExtractList();
        });

        ExtractList.SelectionChanged += OnExtractSelectionChanged;
        ClearSelection.Click += (_, _) => ExtractList.SelectedItem = null;

        BuildLayerToggles();
        BuildHeatmapControls();

        // Hover highlighting and click-to-select on the map itself.
        _canvas.PointerMovedOverMap += OnPointerMovedOverMap;
        _canvas.Clicked += OnMapClicked;

        SuggestionAccept.Click += OnSuggestionAccepted;
        SuggestionDismiss.Click += (_, _) => HideSuggestion();

        // Any manual pan or zoom means the user has taken over; stop yanking the view around.
        _canvas.UserInteracted += (_, _) =>
        {
            if (!FollowToggle.IsChecked.GetValueOrDefault())
                return;

            FollowToggle.IsChecked = false;
            _settings.FollowPlayer = false;
        };
    }

    private void WireSession()
    {
        _session.Status += (_, message) => Dispatcher.UIThread.Post(() => StatusText.Text = message);

        _session.Player.RaidStarted += (_, _) => Dispatcher.UIThread.Post(() =>
            StatusText.Text = "New raid detected; cleared the previous trail.");
        _session.FixApplied += (_, fix) => Dispatcher.UIThread.Post(() => OnFixApplied(fix));
        _session.MapChanged += (_, map) => Dispatcher.UIThread.Post(() => OnMapChanged(map));
        _session.MapSuggested += (_, map) => Dispatcher.UIThread.Post(() => ShowSuggestion(map));

        _session.PoisChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            RebuildExtractList();
            _canvas.InvalidateVisual();
        });
    }

    private async Task ShowPreferencesAsync()
    {
        await new PreferencesWindow(_settings, _session, PersistSettings).ShowDialog(this);

        // Preferences can change things the main window mirrors, so re-read rather than trying to
        // keep the two in sync field by field.
        FollowToggle.IsChecked = _settings.FollowPlayer;
        AlwaysOnTopToggle.IsChecked = _settings.AlwaysOnTop;
        FontSize = _settings.FontSize;

        _canvas.InvalidateVisual();
    }

    private void PersistSettings() => (Avalonia.Application.Current as App)?.SaveSettings();

    /// <summary>Runs a settings edit and persists it, unless controls are still being populated.</summary>
    private void Apply(Action change)
    {
        if (_loadingControls)
            return;

        change();
        PersistSettings();
    }

    // ---- Focus mode ---------------------------------------------------------

    /// <summary>
    /// Turning focus on takes over the view, so remember where we were and hand it back when it
    /// is turned off. Without this, leaving focus mode strands you at whatever zoom the last fix
    /// happened to produce.
    /// </summary>
    private void OnFocusToggled()
    {
        var on = FocusToggle.IsChecked ?? false;
        _settings.ExtractFocusMode = on;

        if (on)
        {
            _viewBeforeFocus = _canvas.Viewport.Capture();
            ApplyExtractFocus();
        }
        else if (_viewBeforeFocus is { } previous)
        {
            _canvas.RestoreView(previous);
            _viewBeforeFocus = null;
        }

        PersistSettings();
    }

    // ---- Exit filter --------------------------------------------------------

    /// <summary>
    /// Drives the exit layers from the filter, then rebuilds the list so the two always agree.
    /// The per-layer checkboxes stay in sync rather than silently disagreeing with the dropdown.
    /// </summary>
    private void ApplyExitFilter()
    {
        var filter = _settings.ExitFilter;

        foreach (var kind in ExtractKinds)
        {
            var on = filter.Includes(kind);
            _session.Pois.Visible[kind] = on;
            _settings.PoiLayers[kind.ToString()] = on;
        }

        RefreshLayerToggles();
        RebuildExtractList();
        _canvas.InvalidateVisual();
    }

    private static readonly PoiKind[] ExtractKinds =
        [PoiKind.ExtractPmc, PoiKind.ExtractScav, PoiKind.ExtractShared, PoiKind.Transit];

    // ---- Exits --------------------------------------------------------------

    private void BuildLayerToggles()
    {
        LayerList.Children.Clear();
        _layerToggles.Clear();

        foreach (var kind in Enum.GetValues<PoiKind>())
        {
            var toggle = new CheckBox
            {
                Content = LayerLabel(kind),
                IsChecked = _session.Pois.IsKindVisible(kind),
                FontSize = 12,
            };

            var captured = kind;
            toggle.IsCheckedChanged += (_, _) => Apply(() =>
            {
                var on = toggle.IsChecked ?? false;
                _session.Pois.Visible[captured] = on;
                _settings.PoiLayers[captured.ToString()] = on;

                // Ticking an exit layer by hand contradicts the dropdown, so drop back to "All"
                // rather than leaving the two showing different things.
                if (ExtractKinds.Contains(captured) && !_settings.ExitFilter.Includes(captured) && on)
                {
                    _settings.ExitFilter = ExitFilter.All;
                    ExitFilterBox.SelectedIndex = (int)ExitFilter.All;
                }

                RebuildExtractList();
                _canvas.InvalidateVisual();
            });

            _layerToggles[kind] = toggle;
            LayerList.Children.Add(toggle);
        }
    }

    /// <summary>Pushes current layer visibility back into the checkboxes without re-firing edits.</summary>
    private void RefreshLayerToggles()
    {
        var wasLoading = _loadingControls;
        _loadingControls = true;

        foreach (var (kind, toggle) in _layerToggles)
            toggle.IsChecked = _session.Pois.IsKindVisible(kind);

        _loadingControls = wasLoading;
    }

    private static string LayerLabel(PoiKind kind) => kind switch
    {
        PoiKind.ExtractPmc => "Exits: PMC",
        PoiKind.ExtractScav => "Exits: Scav",
        PoiKind.ExtractShared => "Exits: shared",
        PoiKind.Transit => "Transits",
        PoiKind.Spawn => "Spawn points",
        PoiKind.BossZone => "Boss zones",
        PoiKind.LootContainer => "Loot containers",
        PoiKind.Hazard => "Hazards",
        PoiKind.Lock => "Locked doors",
        PoiKind.Switch => "Switches",
        PoiKind.StationaryWeapon => "Mounted guns",
        PoiKind.BtrStop => "BTR stops",
        _ => kind.ToString(),
    };

    private void BuildHeatmapControls()
    {
        var heatmap = _session.Heatmap;

        HeatmapToggle.IsChecked = heatmap.IsVisible;
        HeatmapToggle.IsCheckedChanged += (_, _) =>
        {
            heatmap.IsVisible = HeatmapToggle.IsChecked ?? false;
            _settings.ShowHeatmap = heatmap.IsVisible;
            _canvas.InvalidateVisual();
        };

        HeatmapGroups.Children.Clear();
        foreach (var group in Enum.GetValues<SpawnGroup>())
        {
            var toggle = new CheckBox
            {
                Content = HeatmapGroupLabel(group),
                IsChecked = heatmap.Groups.TryGetValue(group, out var on) && on,
                FontSize = 12,
            };

            var captured = group;
            toggle.IsCheckedChanged += (_, _) =>
            {
                var enabled = toggle.IsChecked ?? false;
                heatmap.Groups[captured] = enabled;
                _settings.HeatmapCategories[captured.ToString()] = enabled;
                heatmap.Invalidate();
                _canvas.InvalidateVisual();
            };

            HeatmapGroups.Children.Add(toggle);
        }

        HeatmapRadius.Value = heatmap.RadiusMeters;
        HeatmapRadiusLabel.Text = $"Radius: {heatmap.RadiusMeters:F0} m";
        HeatmapRadius.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty)
                return;

            heatmap.RadiusMeters = HeatmapRadius.Value;
            _settings.HeatmapRadiusMeters = heatmap.RadiusMeters;
            HeatmapRadiusLabel.Text = $"Radius: {heatmap.RadiusMeters:F0} m";
            heatmap.Invalidate();
            _canvas.InvalidateVisual();
        };

        HeatmapOpacity.Value = heatmap.Opacity;
        HeatmapOpacityLabel.Text = $"Opacity: {heatmap.Opacity:P0}";
        HeatmapOpacity.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty)
                return;

            heatmap.Opacity = HeatmapOpacity.Value;
            _settings.HeatmapOpacity = heatmap.Opacity;
            HeatmapOpacityLabel.Text = $"Opacity: {heatmap.Opacity:P0}";
            _canvas.InvalidateVisual();
        };
    }

    private static string HeatmapGroupLabel(SpawnGroup group) => group switch
    {
        SpawnGroup.Pmc => "PMC players",
        SpawnGroup.Scav => "Scavs",
        SpawnGroup.AiPmc => "AI PMCs",
        SpawnGroup.Boss => "Bosses",
        _ => group.ToString(),
    };

    private void RebuildExtractList()
    {
        var all = _session.Pois.Extracts.ToArray();

        // Distances are shown whether or not we sort by them; knowing an exit is 40 m away is
        // useful even in the faction-grouped order.
        var player = _session.Player.Current?.Position;
        foreach (var poi in all)
            poi.DistanceMeters = player?.GroundDistanceTo(poi.Position);

        var visible = all.Where(p => _settings.ExitFilter.Includes(p.Kind));

        // Sorting by distance is meaningless until a screenshot has placed the player, so fall
        // back to the stable ordering rather than showing an arbitrary one.
        var sortByDistance = _settings.SortExitsByDistance && player is not null;

        var exits = (sortByDistance
                ? visible.OrderBy(p => p.DistanceMeters ?? double.MaxValue)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                : visible.OrderBy(p => p.Kind)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // Keep a selection the filter would otherwise hide, so choosing "Running as PMC" while
        // already heading for a Scav exit does not quietly drop the guide line.
        var selected = _session.SelectedExtract;
        if (selected is not null && !exits.Contains(selected))
            exits.Insert(0, selected);

        _suppressExtractSelectionEvent = true;
        ExtractList.ItemsSource = exits;
        ExtractList.SelectedItem = selected;
        _suppressExtractSelectionEvent = false;

        var hidden = all.Length - exits.Count;

        ExitHint.Text = all.Length == 0
            ? "No exit data for this map yet."
            : hidden > 0
                ? $"{exits.Count} of {all.Length} exits shown, {hidden} hidden by the filter."
                : $"{exits.Count} exits. Pick one to draw a guide line from the player.";

        UpdateExtractDetail();
    }

    private void OnExtractSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressExtractSelectionEvent)
            return;

        _session.SelectedExtract = ExtractList.SelectedItem as MapPoi;
        UpdateExtractDetail();
        ApplyExtractFocus();
        _canvas.InvalidateVisual();
    }

    private void UpdateExtractDetail()
    {
        var selected = _session.SelectedExtract;

        ExtractDetail.IsVisible = selected is not null;
        if (selected is null)
            return;

        DetailName.Text = selected.Name;

        DetailFaction.Text = selected.DestinationMap is { } destination
            ? $"Transit to {GameMap.ToDisplayName(destination)}"
            : selected.FactionLabel;

        var distance = _session.ExtractLine.DistanceMeters;
        var bearing = _session.ExtractLine.RelativeBearingDegrees;

        if (distance is null)
        {
            DetailDistance.Text = "Waiting for a screenshot to place you.";
        }
        else
        {
            var turn = bearing is null || Math.Abs(bearing.Value) < 4
                ? "ahead"
                : $"{Math.Abs(bearing.Value):F0}° {(bearing < 0 ? "left" : "right")}";

            DetailDistance.Text = $"{distance:F0} m away, {turn}";
        }

        DetailConditions.ItemsSource = selected.Details;

        DetailElevation.IsVisible = selected.Elevation is not null;
        if (selected.Elevation is { } elevation)
            DetailElevation.Text = $"Elevation {elevation.Bottom:F1} to {elevation.Top:F1}";
    }

    /// <summary>
    /// Frames the player and the selected exit together. Called on selection and on every new fix,
    /// so the view tightens as the player closes in.
    /// </summary>
    private void ApplyExtractFocus()
    {
        if (!FocusToggle.IsChecked.GetValueOrDefault())
            return;

        if (_session.SelectedExtract is not { } target || _session.Player.Current is not { } fix)
            return;

        if (_canvas.Map is not { } map)
            return;

        _canvas.FrameBoth(map.ToBase(fix.Position), target.Base, _settings.ExtractFocusPadding);
    }

    private void OnPointerMovedOverMap(object? sender, MapPoint? position)
    {
        var hovered = position is { } p
            ? _session.Pois.HitTest(_canvas.Viewport, _canvas.Viewport.ToScreen(p).X, _canvas.Viewport.ToScreen(p).Y)
            : null;

        if (ReferenceEquals(hovered, _session.Pois.Hovered))
            return;

        _session.Pois.Hovered = hovered;

        // A compact tooltip beats a panel round-trip for something that changes on every mouse move.
        _canvas.SetValue(ToolTip.TipProperty, hovered is null ? null : DescribeForTooltip(hovered));
        _canvas.InvalidateVisual();
    }

    private string DescribeForTooltip(MapPoi poi)
    {
        var lines = new List<string> { poi.Name };

        if (!string.IsNullOrEmpty(poi.FactionLabel))
            lines.Add(poi.FactionLabel);

        if (_session.Player.Current is { } fix)
            lines.Add($"{fix.Position.GroundDistanceTo(poi.Position):F0} m away");

        lines.AddRange(poi.Details);

        return string.Join(Environment.NewLine, lines);
    }

    private void OnMapClicked(object? sender, MapPoint position)
    {
        var screen = _canvas.Viewport.ToScreen(position);
        var hit = _session.Pois.HitTest(_canvas.Viewport, screen.X, screen.Y);

        // Only exits are selectable; clicking a loot marker should not clear the current exit.
        if (hit is null || (!hit.IsExtract && hit.Kind != PoiKind.Transit))
            return;

        ExtractList.SelectedItem = hit;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        _canvas.AddOverlay(_session.Heatmap);
        _canvas.AddOverlay(_session.Pois);
        _canvas.AddOverlay(_session.ExtractLine);
        _canvas.AddOverlay(_session.Player);

        try
        {
            await _session.StartAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Startup failed: {ex.Message}";
        }
    }

    // ---- Map --------------------------------------------------------------

    private async void OnMapSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressMapSelectorEvent || MapSelector.SelectedItem is not GameMap map)
            return;

        HideSuggestion();

        try
        {
            await _session.SetMapAsync(map);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not load {map.DisplayName}: {ex.Message}";
        }
    }

    private void OnMapChanged(GameMap map)
    {
        if (_session.ImageSource is { } source)
            _canvas.SetMap(map, source);

        _canvas.ShowBaseLayer = _settings.ShowBaseLayer;

        _session.Player.ActiveFloors = _canvas.ActiveFloors;

        BuildFloorList(map);

        _suppressMapSelectorEvent = true;
        MapSelector.SelectedItem = map;
        _suppressMapSelectorEvent = false;

        _canvas.InvalidateVisual();
    }

    private void BuildFloorList(GameMap map)
    {
        FloorList.Children.Clear();
        FloorPanel.IsVisible = map.Floors.Count > 0;

        if (map.Floors.Count == 0)
            return;

        // Ground is toggleable, not decorative. The artwork stacks floors as opaque geometry, so
        // an underground level is completely hidden behind the ground floor until this is off --
        // Factory's Tunnels being the obvious case.
        var ground = new CheckBox
        {
            Content = "Ground",
            IsChecked = _canvas.ShowBaseLayer,
            [ToolTip.TipProperty] = "Turn off to see a level underneath the ground floor",
        };

        ground.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _canvas.ShowBaseLayer = ground.IsChecked ?? false;
            _settings.ShowBaseLayer = _canvas.ShowBaseLayer;
            UpdateFloorHint();
            _canvas.InvalidateVisual();
        });

        FloorList.Children.Add(ground);

        foreach (var floor in map.Floors)
        {
            var toggle = new CheckBox
            {
                Content = floor.Name,
                IsChecked = _canvas.ActiveFloors.Contains(floor.Name),
            };

            var name = floor.Name;
            toggle.IsCheckedChanged += (_, _) =>
            {
                if (toggle.IsChecked.GetValueOrDefault())
                    _canvas.ActiveFloors.Add(name);
                else
                    _canvas.ActiveFloors.Remove(name);

                _session.Player.ActiveFloors = _canvas.ActiveFloors;
                UpdateFloorHint();
                _canvas.InvalidateVisual();
            };

            FloorList.Children.Add(toggle);
        }

        UpdateFloorHint();
    }

    /// <summary>
    /// Warns when every level is switched off, which draws an empty map. That is a legitimate
    /// state to pass through while switching floors, so it is a hint rather than a refusal.
    /// </summary>
    private void UpdateFloorHint()
    {
        var nothingShown = !_canvas.ShowBaseLayer && _canvas.ActiveFloors.Count == 0;

        FloorHint.IsVisible = nothingShown;
        FloorHint.Text = nothingShown ? "All levels are off, so the map is blank." : "";
    }

    // ---- Fixes ------------------------------------------------------------

    private void OnFixApplied(PlayerFix fix)
    {
        var elapsed = _session.Player.RaidElapsed;

        PositionText.Text =
            $"{fix.Position.X,8:F1} {fix.Position.Y,7:F1} {fix.Position.Z,8:F1}   " +
            $"{fix.YawDegrees,5:F0}°   raid {fix.RaidTimeDisplay}   " +
            $"+{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";

        UpdateExtractDetail();

        // Distances in the list are relative to the player, so a new fix changes all of them --
        // and reorders the list entirely when sorting by distance.
        RebuildExtractList();

        // Focus mode owns the view when it is on, so following would fight it.
        if (FocusToggle.IsChecked.GetValueOrDefault() && _session.SelectedExtract is not null)
            ApplyExtractFocus();
        else if (FollowToggle.IsChecked.GetValueOrDefault() && _canvas.Map is { } map)
            _canvas.Viewport.Center = map.ToBase(fix.Position);

        _canvas.InvalidateVisual();
    }

    // ---- Map suggestion ---------------------------------------------------

    private void ShowSuggestion(GameMap map)
    {
        if (_settings.AutoSwitchMap)
        {
            MapSelector.SelectedItem = map;
            return;
        }

        _suggestedMap = map;
        SuggestionText.Text = $"That looks like {map.DisplayName}.";
        SuggestionBar.IsVisible = true;
    }

    private void OnSuggestionAccepted(object? sender, RoutedEventArgs e)
    {
        if (_suggestedMap is { } map)
            MapSelector.SelectedItem = map;

        HideSuggestion();
    }

    private void HideSuggestion()
    {
        _suggestedMap = null;
        SuggestionBar.IsVisible = false;
    }

    // ---- Theme ------------------------------------------------------------

    private void OnThemeClicked(object? sender, RoutedEventArgs e)
    {
        _settings.Theme = _settings.Theme switch
        {
            AppTheme.Dark => AppTheme.Light,
            AppTheme.Light => AppTheme.System,
            _ => AppTheme.Dark,
        };

        if (Avalonia.Application.Current is App app)
        {
            app.ApplyTheme(_settings.Theme);
            app.SaveSettings();
        }

        StatusText.Text = $"Theme: {_settings.Theme}";
    }
}
