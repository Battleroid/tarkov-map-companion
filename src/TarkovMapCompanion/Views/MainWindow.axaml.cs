using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Diagnostics;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Party;
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

    /// <summary>Ticks peer ages, which change with no event to hang off.</summary>
    private DispatcherTimer? _partyClock;

    /// <summary>Whether the floating map panels are open. Kept across map switches, not restarts.</summary>
    private bool _floorOverlayExpanded;
    private bool _partyOverlayExpanded;
    private bool _layersOverlayExpanded;

    /// <summary>
    /// Whether the party panel has already opened itself once for the session that is running.
    /// </summary>
    /// <remarks>
    /// It opens on the way into hosting, joining or failing, so a session code is there to copy
    /// rather than behind a click. Once only: reopening a panel the user has deliberately shut,
    /// every time the roster changes, would be its own kind of irritating.
    /// </remarks>
    private bool _partyOverlayAutoExpanded;

    /// <summary>Repaints for everything that animates, and stops when nothing does.</summary>
    private MapClock? _mapClock;

    /// <summary>The pop-out minimap, or null when it is closed.</summary>
    private MinimapWindow? _minimap;

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
        //
        // Index 0, not Add: MapHost's XAML children are the panels floating over the map, and a
        // Panel z-orders by child order. Appending the canvas buries them underneath it, where they
        // are both invisible and unclickable -- which looks exactly like the XAML having done
        // nothing at all.
        MapHost.Children.Insert(0, _canvas);

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

            // Before the session goes: the minimap unsubscribes from it on the way out, and it has
            // no taskbar entry, so leaving it open would strand a window nobody can close.
            _minimap?.Close();

            _mapClock?.Dispose();
            _partyClock?.Stop();
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
        AlwaysOnTopToggle.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _settings.AlwaysOnTop = AlwaysOnTopToggle.IsChecked ?? false;
            Topmost = _settings.AlwaysOnTop;
        });

        // Through Apply so it lands on disk when it changes. Both of these used to write the
        // setting and rely on the save at shutdown, which meant an ungraceful exit silently
        // reverted them.
        FollowToggle.IsChecked = _settings.FollowPlayer;
        FollowToggle.IsCheckedChanged += (_, _) => Apply(() =>
            _settings.FollowPlayer = FollowToggle.IsChecked ?? false);

        MinimapToggle.IsCheckedChanged += (_, _) => OnMinimapToggled();

        ThemeButton.Click += OnThemeClicked;
        FitButton.Click += (_, _) => _canvas.FitAll();

        PreferencesButton.Click += async (_, _) => await ShowPreferencesAsync();
        AboutButton.Click += async (_, _) => await new AboutWindow(_catalog).ShowDialog(this);

        FocusToggle.IsChecked = _settings.ExtractFocusMode;
        FocusToggle.IsCheckedChanged += (_, _) => OnFocusToggled();

        // Marker mode is deliberately not persisted. It is a thing you are doing right now, not a
        // preference, and an app that reopened still armed would swallow the first map click.
        MarkToggle.IsCheckedChanged += (_, _) => OnMarkModeToggled();

        ClearMarksButton.Click += (_, _) =>
        {
            _session.ClearWaypoints();
            StatusText.Text = "Markers cleared.";
        };

        _canvas.SmoothMovement = _settings.SmoothCameraMovement;
        Audio.PingSound.Enabled = _settings.PingSound;

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

        ReadExitsToggle.IsChecked = _settings.ReadExitsFromScreenshots;
        ReadExitsToggle.IsCheckedChanged += (_, _) => Apply(OnReadExitsToggled);

        ClearReadExitsButton.Click += (_, _) =>
        {
            _session.ClearExitAvailability();
            UpdateExitAvailabilityBar();
            RebuildExtractList();
            _canvas.InvalidateVisual();
        };

        ExtractList.SelectionChanged += OnExtractSelectionChanged;
        ExtractList.DoubleTapped += OnExtractListDoubleTapped;

        // Both map overlays start collapsed and are not persisted. Like marker mode, an expanded
        // panel is a thing you are doing now rather than a preference -- but the state is kept in a
        // field across map switches, so somebody playing Factory all evening is not reopening the
        // level list every raid.
        FloorOverlayToggle.IsCheckedChanged += (_, _) =>
            FloorBody.IsVisible = _floorOverlayExpanded = FloorOverlayToggle.IsChecked ?? false;

        PartyOverlayToggle.IsCheckedChanged += (_, _) =>
            PartyBody.IsVisible = _partyOverlayExpanded = PartyOverlayToggle.IsChecked ?? false;

        LayersOverlayToggle.IsCheckedChanged += (_, _) =>
            LayersBody.IsVisible = _layersOverlayExpanded = LayersOverlayToggle.IsChecked ?? false;

        // At the minimum window width, minus the 290px sidebar, the map is about 530px across and
        // an open 250px party panel takes half of it. Shut it rather than cover the map; the pill
        // stays, so nothing becomes unreachable.
        MapHost.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width >= 640 || !_partyOverlayExpanded)
                return;

            PartyOverlayToggle.IsChecked = false;
        };

        BuildLayerToggles();
        BuildHeatmapControls();
        WireParty();

        // Hover highlighting and click-to-select on the map itself.
        _canvas.PointerMovedOverMap += OnPointerMovedOverMap;
        _canvas.Clicked += OnMapClicked;

        SuggestionAccept.Click += OnSuggestionAccepted;
        SuggestionDismiss.Click += (_, _) => HideSuggestion();

        // Panning and zooming used to switch Follow Player off, on the theory that touching the map
        // means taking over. In a raid it means the opposite: you look at a corner of the map
        // between screenshots precisely because you expect to be put back when the next one lands,
        // and having to notice a checkbox had silently unticked itself was worse than any amount of
        // being recentered. Only the button turns it off now, and Focus Exit has always worked this
        // way -- so both view-owning modes finally obey one rule.
        Escape();
    }

    /// <summary>
    /// Opens or closes the pop-out minimap.
    /// </summary>
    /// <remarks>
    /// Shown rather than shown-as-dialog, so the main window stays usable behind it, and it is not
    /// owned by this window either -- an owned window is forced above its owner but also minimizes
    /// with it, which is precisely wrong for something meant to sit over a game while the main
    /// window is out of the way.
    /// </remarks>
    private void OnMinimapToggled()
    {
        var wanted = MinimapToggle.IsChecked ?? false;

        if (!wanted)
        {
            _minimap?.Close();
            return;
        }

        if (_minimap is not null)
            return;

        _minimap = new MinimapWindow(_settings, _session, PersistSettings);

        _minimap.Dismissed += (_, _) => Post(() =>
        {
            _minimap = null;
            MinimapToggle.IsChecked = false;
        });

        _minimap.Show();
    }

    /// <summary>Escape leaves marker mode, which is easy to forget you are in.</summary>
    private void Escape() => KeyDown += (_, e) =>
    {
        if (e.Key is not Avalonia.Input.Key.Escape || MarkToggle.IsChecked != true)
            return;

        MarkToggle.IsChecked = false;
        e.Handled = true;
    };

    private void WireSession()
    {
        _session.Status += (_, message) => Post(() => StatusText.Text = message);

        _session.Player.RaidStarted += (_, _) => Post(() =>
            StatusText.Text = "New raid detected; cleared the previous trail.");
        _session.FixApplied += (_, fix) => Post(() => OnFixApplied(fix));
        _session.MapChanged += (_, map) => Post(() => OnMapChanged(map));
        _session.MapSuggested += (_, map) => Post(() => ShowSuggestion(map));
        _session.MapDetectedFromLog += (_, map) => Post(() => OnMapDetectedFromLog(map));
        _session.RaidStateChanged += (_, started) => Post(() => OnRaidStateChanged(started));

        _session.PoisChanged += (_, _) => Post(() =>
        {
            RebuildExtractList();
            _canvas.InvalidateVisual();
        });

        _session.Party.Changed += (_, _) => Post(() =>
        {
            UpdatePartyPanel();
            _canvas.InvalidateVisual();
        });

        _session.WaypointsChanged += (_, _) => Post(() =>
        {
            UpdateWaypointControls();
            UpdateExtractDetail();

            // A second pin is what gives the route a line for the arrows to march along.
            _mapClock?.Wake();
            _canvas.InvalidateVisual();
        });

        _session.ExitAvailabilityChanged += (_, availability) => Post(() =>
        {
            UpdateExitAvailabilityBar();
            RebuildExtractList();
            _canvas.InvalidateVisual();

            if (availability is not null)
                StatusText.Text = $"Read {availability.NameCount} exits from the screenshot.";
        });
    }

    /// <summary>
    /// Marshals to the UI thread and contains anything that throws there.
    /// </summary>
    /// <remarks>
    /// These callbacks all originate on the folder-watcher thread. An exception escaping a posted
    /// action reaches the dispatcher loop and closes the window with no message, which from the
    /// outside looks like "I took a screenshot and the app vanished". Failing one update is
    /// recoverable; losing the app mid-raid is not.
    /// </remarks>
    private void Post(Action action) => Dispatcher.UIThread.Post(() =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("UI update failed", ex);
            StatusText.Text = $"Something went wrong updating the view; see {Log.Path}";
        }
    });

    private async Task ShowPreferencesAsync()
    {
        await new PreferencesWindow(_settings, _session, PersistSettings).ShowDialog(this);

        // Preferences can change things the main window mirrors, so re-read rather than trying to
        // keep the two in sync field by field. Follow Player is deliberately absent: it lives on
        // the toolbar and nowhere else, so there is nothing to sync back.
        AlwaysOnTopToggle.IsChecked = _settings.AlwaysOnTop;
        FontSize = _settings.FontSize;

        _canvas.SmoothMovement = _settings.SmoothCameraMovement;
        Audio.PingSound.Enabled = _settings.PingSound;
        _session.Waypoints.ArrivalRadiusMeters = _settings.WaypointArrivalRadiusMeters;
        _session.Waypoints.Arrival = _settings.WaypointArrival;
        _session.Waypoints.AnimateArrows = _settings.AnimateRouteArrows;
        _session.Player.MarkerSize = (float)_settings.PlayerMarkerSize;
        _session.Player.Color = ColorCodec.Parse(_settings.PlayerColor, MarkerPalette.Player);
        _session.ExtractLine.Color = ColorCodec.Parse(_settings.GuideLineColor, MarkerPalette.ExtractLine);

        // Both live on the minimap and are reachable from here, so they have to be pushed across
        // when the dialog closes rather than waiting for it to be reopened.
        _minimap?.ApplyClickThrough();
        _minimap?.Rescale();

        // The roster's own swatch follows the player color, and a route may have started animating.
        UpdatePartyPanel();
        _mapClock?.Wake();
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

    // ---- Party --------------------------------------------------------------

    private void WireParty()
    {
        HostPartyButton.Click += async (_, _) => await StartHostingAsync();
        JoinPartyButton.Click += async (_, _) => await JoinPartyAsync();
        LeavePartyButton.Click += (_, _) => _session.Party.Leave();

        JoinCodeBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
                await JoinPartyAsync();
        };

        CopyCodeButton.Click += async (_, _) =>
        {
            if (_session.Party.Code is { } code && Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(code);
                StatusText.Text = "Session code copied.";
            }
        };

        // Ages have to tick on their own: a teammate who stops taking screenshots produces no
        // events, and that is precisely when their marker most needs to be visibly going stale.
        _partyClock = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            if (!_session.Party.IsActive)
                return;

            UpdatePartyPanel();
            _canvas.InvalidateVisual();
        });

        _partyClock.Start();

        // Pings and route arrows animate, so they need frames rather than events. The clock only
        // runs while one of them says it is still moving; an idle map should not be repainting
        // twenty times a second forever, least of all on a second monitor beside a running game.
        // One clock drives both windows. A second timer for the minimap would double the wake-ups
        // for animations that are already in lockstep, since both canvases read the same overlays.
        _mapClock = new MapClock(() =>
        {
            _canvas.InvalidateVisual();
            _minimap?.Redraw();
        });
        _mapClock.Register(_session.Pings);
        _mapClock.Register(_session.Waypoints);

        // Frames nobody can see are not worth the wake-ups.
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty || _mapClock is null)
                return;

            _mapClock.Suspended = WindowState == WindowState.Minimized;
            _mapClock.Wake();
        };

        _session.PingAdded += (_, ping) => Post(() =>
        {
            Audio.PingSound.Play();

            StatusText.Text = $"{ping.Name} pinged the map.";
            _mapClock?.Wake();
            _canvas.InvalidateVisual();
        });

        // Findable before anything has been tried, because when it does become relevant the answer
        // should already be on screen. Cut to the two facts somebody types into a router: most
        // routers open the port themselves and never make this matter, and the full explanation is
        // in Settings and in the warning that appears when hosting actually cannot open it.
        var lan = Party.PortMapper.LocalAddress();

        PartyIdleHint.Text = lan is null
            ? $"If your router will not open TCP {_settings.PartyPort}, forward it to this PC."
            : $"If your router will not open TCP {_settings.PartyPort}, forward it to {lan}.";
    }

    private async Task StartHostingAsync()
    {
        HostPartyButton.IsEnabled = false;

        try
        {
            await _session.Party.HostAsync(DisplayName(), _settings.PartyPort);
        }
        finally
        {
            HostPartyButton.IsEnabled = true;
        }
    }

    private async Task JoinPartyAsync()
    {
        var code = JoinCodeBox.Text;

        if (string.IsNullOrWhiteSpace(code))
        {
            StatusText.Text = "Paste the session code first.";
            return;
        }

        JoinPartyButton.IsEnabled = false;

        try
        {
            if (await _session.Party.JoinAsync(code, DisplayName()))
                JoinCodeBox.Text = "";
        }
        finally
        {
            JoinPartyButton.IsEnabled = true;
        }
    }

    /// <summary>Falls back to the Windows username so nobody has to fill anything in first.</summary>
    private string DisplayName() =>
        string.IsNullOrWhiteSpace(_settings.PlayerName) ? Environment.UserName : _settings.PlayerName;

    /// <summary>Lets the advice sentence be reused mid-sentence without reading as a new one.</summary>
    private static string Uncapitalized(string text) =>
        text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    /// <summary>The port and address to forward to, spelled out rather than left to be looked up.</summary>
    private string ForwardingAdvice(int port) =>
        _session.Party.LocalAddress is { } address
            ? $"Forward TCP {port} to {address} (this PC) in your router, then try again."
            : $"Forward TCP {port} to this PC in your router, then try again.";

    private void UpdatePartyPanel()
    {
        var party = _session.Party;
        var active = party.IsActive;

        PartyIdlePanel.IsVisible = !active;
        PartyActivePanel.IsVisible = active;
        PartyCodePanel.IsVisible = party.Code is not null;

        if (party.Code is { } code)
            PartyCodeText.Text = code;

        PartyFailedPanel.IsVisible = party.State == PartyState.Failed && party.ListenPort > 0;
        if (PartyFailedPanel.IsVisible)
        {
            PartyFailedText.Text =
                "Could not open a port automatically. "
                + ForwardingAdvice(party.ListenPort)
                + " Failing that, have someone else host.";
        }

        LeavePartyButton.Content = party.State == PartyState.Hosting ? "Stop session" : "Leave session";

        PartyRoster.ItemsSource = party.Peers.Select(BuildRow).ToArray();

        // Facts, not reassurance. What somebody wants from this line while a session is up is where
        // they are reachable; the privacy note it used to carry is in the README and the Settings
        // screen, which is where you read about a feature rather than while using it.
        PartyHint.Text = party.State switch
        {
            PartyState.Starting => "Opening a port...",
            PartyState.Joining => "Connecting...",
            PartyState.Hosting when party.PublicEndpoint is { } endpoint => $"Hosting on {endpoint} (TCP)",
            PartyState.Hosting => $"Hosting on port {party.ListenPort} (TCP)",
            PartyState.Joined => "Connected.",
            _ => "",
        };

        PartyHint.IsVisible = PartyHint.Text.Length > 0;

        // Forwarding advice hangs off that one line as a tooltip rather than occupying a bordered
        // block of its own. It used to be three sentences repeating the port and address that the
        // line above already showed, and it was on screen for the whole session -- for a thing most
        // routers make irrelevant, and which you only go looking for once somebody cannot connect.
        //
        // The line does turn amber when the router refused, because that is the case where
        // everything looks fine right up until nobody can join, and it should not take a hover to
        // find out.
        var openedItself = party.State != PartyState.Hosting || party.RouterOpenedPort;

        PartyHint.Foreground = openedItself
            ? this.FindResource("TextSecondaryBrush") as IBrush
            : this.FindResource("WarningBrush") as IBrush;

        ToolTip.SetTip(PartyHint, party.State != PartyState.Hosting
            ? null
            : openedItself
                ? $"Your router opened this port. If nobody can connect, {Uncapitalized(ForwardingAdvice(party.ListenPort))}"
                : $"Your router would not open this port. {ForwardingAdvice(party.ListenPort)} "
                  + "Only the host needs this; people joining you open nothing.");

        // The pill carries the roster count, so collapsed still answers "is anyone here".
        PartyOverlayToggle.Content = active ? $"Party · {party.Peers.Count}" : "Party";

        // Anything that has to be read comes to full strength. The rest of the time it stays out of
        // the way of the map, which is the thing actually being looked at.
        var needsAttention = party.State is PartyState.Starting or PartyState.Joining or PartyState.Failed
                             || !openedItself;

        PartyOverlay.Classes.Set("attention", needsAttention);

        // Open once when a session starts or fails, so the code is there to copy and a failure is
        // not hidden behind a collapsed panel.
        if (party.State is PartyState.Idle)
        {
            _partyOverlayAutoExpanded = false;
        }
        else if (!_partyOverlayAutoExpanded)
        {
            _partyOverlayAutoExpanded = true;
            PartyOverlayToggle.IsChecked = true;
        }
    }

    private PartyRow BuildRow(PartyPeer peer)
    {
        var color = _session.Peers.ColorFor(peer.Name);

        var detail = peer switch
        {
            { IsSelf: true } => "you",
            { HasPosition: false } => "no position yet",

            // Named rather than hidden. Knowing a teammate is on another map is useful; drawing
            // them in this map's coordinates would not be.
            var p when !string.Equals(p.Map, _session.CurrentMap.NormalizedName, StringComparison.OrdinalIgnoreCase)
                => $"on {GameMap.ToDisplayName(p.Map)}",

            var p when p.AgeSeconds < 20 => "now",
            var p when p.AgeSeconds < 90 => $"{p.AgeSeconds:F0}s ago",
            var p => $"{p.AgeSeconds / 60:F0}m ago",
        };

        // Your own row shows the color you actually draw in, so the roster and the map agree.
        var own = _session.Player.Color;
        var swatch = peer.IsSelf
            ? new SolidColorBrush(Color.FromRgb(own.Red, own.Green, own.Blue))
            : new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue));

        return new PartyRow(peer.Name, detail, swatch);
    }

    // ---- Route markers ------------------------------------------------------

    private void OnMarkModeToggled()
    {
        var on = MarkToggle.IsChecked ?? false;
        _session.Waypoints.IsPlacing = on;

        // A crosshair is the only thing telling the user that a click means something different
        // now, since the map itself looks identical.
        _canvas.Cursor = new Cursor(on ? StandardCursorType.Cross : StandardCursorType.Arrow);

        StatusText.Text = on
            ? "Marker mode: click the map in the order you want to visit. Click Mark Route again, or press Escape, to finish."
            : DescribeRoute();
    }

    private string DescribeRoute()
    {
        var count = _session.Waypoints.Count;

        return count == 0
            ? "No markers set."
            : $"{count} marker{(count == 1 ? "" : "s")} to visit before the exit.";
    }

    private void UpdateWaypointControls()
    {
        var count = _session.Waypoints.Count;

        ClearMarksButton.IsVisible = count > 0;
        ClearMarksButton.Content = $"Clear marks ({count})";
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

    // ---- Exits read from the screenshot -------------------------------------

    /// <summary>
    /// Arms or disarms reading the in-game extraction panel out of new screenshots.
    /// </summary>
    /// <remarks>
    /// Checks for a working OCR engine at the moment it is switched on, rather than letting the
    /// box sit there looking armed while quietly doing nothing for a whole raid.
    /// </remarks>
    private void OnReadExitsToggled()
    {
        var on = ReadExitsToggle.IsChecked ?? false;

        if (on && _session.ExitReaderUnavailableReason is { } reason)
        {
            ReadExitsToggle.IsChecked = false;
            _settings.ReadExitsFromScreenshots = false;
            StatusText.Text = reason;
            return;
        }

        _settings.ReadExitsFromScreenshots = on;

        if (on)
        {
            StatusText.Text = "Bring up the extraction list in game (double-tap O) and take a screenshot.";
            return;
        }

        _session.ClearExitAvailability();
        UpdateExitAvailabilityBar();
        RebuildExtractList();
        _canvas.InvalidateVisual();
    }

    private void UpdateExitAvailabilityBar()
    {
        var availability = _session.ExitAvailability;

        ReadExitsBar.IsVisible = availability is not null;

        if (availability is null)
            return;

        var exits = _session.Pois.Extracts.ToArray();
        var shown = exits.Count(availability.Includes);

        var text = $"{availability.TakenAt:HH:mm} screenshot: {shown} of {exits.Length} exits open this raid.";

        // A row we could not place is reported rather than swallowed: the honest reading is
        // "the app did not understand this", not "that exit is unavailable".
        if (availability.Unresolved.Count > 0)
            text += $" Unrecognized: {string.Join(", ", availability.Unresolved)}.";

        ReadExitsText.Text = text;
    }

    private void RebuildExtractList()
    {
        var all = _session.Pois.Extracts.ToArray();

        // Distances are shown whether or not we sort by them; knowing an exit is 40 m away is
        // useful even in the faction-grouped order.
        var player = _session.Player.Current?.Position;
        var availability = _session.ExitAvailability;

        foreach (var poi in all)
        {
            poi.DistanceMeters = player?.GroundDistanceTo(poi.Position);
            poi.AvailableThisRaid = availability?.Includes(poi);
        }

        var visible = all.Where(p => _settings.ExitFilter.Includes(p.Kind));

        // Sorting by distance is meaningless until a screenshot has placed the player, so fall
        // back to the stable ordering rather than showing an arbitrary one.
        var sortByDistance = _settings.SortExitsByDistance && player is not null;

        // Exits the raid actually offers float to the top, whichever secondary order is in use.
        // Without a reading every entry is null here and the ordering is untouched.
        var exits = (sortByDistance
                ? visible.OrderBy(p => p.AvailableThisRaid == false)
                    .ThenBy(p => p.DistanceMeters ?? double.MaxValue)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                : visible.OrderBy(p => p.AvailableThisRaid == false)
                    .ThenBy(p => p.Kind)
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

    /// <summary>
    /// Decides which rows are showing their requirements.
    /// </summary>
    /// <remarks>
    /// Only the selected exit ever expands, and only when the preference allows it and the row has
    /// not been folded away by double-clicking. Everything else collapses, so the list cannot grow
    /// into a wall of requirement text as you click down it.
    /// </remarks>
    private void UpdateExtractDetail()
    {
        var selected = _session.SelectedExtract;

        foreach (var poi in ExtractList.ItemsSource?.OfType<MapPoi>() ?? [])
        {
            if (!ReferenceEquals(poi, selected))
                poi.DetailsExpanded = false;
        }

        if (selected is not null)
            selected.DetailsExpanded = _settings.ShowExitConditions && selected.HasDetails;

        RefreshExtractRows();
    }

    /// <summary>
    /// Redraws the list so the row-level expansion flags take effect.
    /// </summary>
    /// <remarks>
    /// <see cref="MapPoi"/> raises no change notifications by design -- the type's own comment says
    /// the list is rebuilt whenever its mutable fields change, and this is that rebuild. Selection
    /// is reassigned rather than left to survive, because replacing ItemsSource drops it.
    /// </remarks>
    private void RefreshExtractRows()
    {
        if (ExtractList.ItemsSource is not IEnumerable<MapPoi> rows)
            return;

        var selected = ExtractList.SelectedItem;
        var items = rows.ToArray();

        _suppressExtractSelectionEvent = true;
        ExtractList.ItemsSource = items;
        ExtractList.SelectedItem = selected;
        _suppressExtractSelectionEvent = false;
    }

    /// <summary>
    /// Double-clicking a row folds its requirements away, or brings them back.
    /// </summary>
    /// <remarks>
    /// Per-row rather than a global preference, because "I have read this one" is about this exit
    /// and not about every exit. The Settings checkbox still decides whether a row starts expanded.
    /// </remarks>
    private void OnExtractListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_session.SelectedExtract is not { } selected || !selected.HasDetails)
            return;

        selected.DetailsExpanded = !selected.DetailsExpanded;
        RefreshExtractRows();
    }

    /// <summary>
    /// Frames the player and the selected exit together. Called on selection and on every new fix,
    /// so the view tightens as the player closes in.
    /// </summary>
    private void ApplyExtractFocus()
    {
        if (!FocusToggle.IsChecked.GetValueOrDefault())
            return;

        // Frames whatever the guide line is pointing at, which is the next marker when a route is
        // set and the chosen exit otherwise. Framing the exit while being routed to a marker would
        // zoom out past everything that currently matters.
        if (_session.ExtractLine.GuideBase is not { } target || _session.Player.Current is not { } fix)
            return;

        if (_canvas.Map is not { } map)
            return;

        _canvas.FrameBoth(map.ToBase(fix.Position), target, _settings.ExtractFocusPadding);
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

        // Folded away with the detail panel's copy, or hovering would put back the clutter the
        // panel was just told to hide.
        if (_settings.ShowExitConditions)
            lines.AddRange(poi.Details);
        else if (poi.Details.Count > 0)
            lines.Add($"{poi.Details.Count} condition{(poi.Details.Count == 1 ? "" : "s")}");

        return string.Join(Environment.NewLine, lines);
    }

    private void OnMapClicked(object? sender, MapClick click)
    {
        var position = click.Position;

        // Shift-click pings, whatever else is going on. It is the one action you might want in a
        // hurry, so it should not depend on being in the right mode first.
        if (click.IsShift)
        {
            _session.SendPing(position);
            return;
        }

        // While placing, a click is a marker and nothing else. Selecting an exit out from under
        // someone laying out a route would be maddening.
        if (_session.Waypoints.IsPlacing)
        {
            _session.AddWaypoint(position);
            StatusText.Text = $"Marker {_session.Waypoints.Count} placed. Click Mark Route again, or press Escape, to finish.";
            return;
        }

        var screen = _canvas.Viewport.ToScreen(position);
        var hit = _session.Pois.HitTest(_canvas.Viewport, screen.X, screen.Y);

        // Only exits are selectable; clicking a loot marker should not clear the current exit.
        if (hit is null || (!hit.IsExtract && hit.Kind != PoiKind.Transit))
            return;

        // Clicking the exit that is already chosen lets it go. Otherwise the only way to drop a
        // selection is a Clear button on the far side of the window, a long way from the marker you
        // were already looking at.
        ExtractList.SelectedItem = ReferenceEquals(hit, _session.SelectedExtract) ? null : hit;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        _canvas.AddOverlay(_session.Heatmap);
        _canvas.AddOverlay(_session.Pois);
        _canvas.AddOverlay(_session.Waypoints);
        _canvas.AddOverlay(_session.ExtractLine);
        _canvas.AddOverlay(_session.Peers);
        _canvas.AddOverlay(_session.Pings);
        _canvas.AddOverlay(_session.Player);

        try
        {
            await _session.StartAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Startup failed: {ex.Message}";
        }

        // Said last so it is what remains on screen. Two copies fight over reading and culling the
        // same folder, and the symptoms look like unrelated bugs rather than like a duplicate.
        if (Program.AnotherInstanceRunning)
        {
            StatusText.Text =
                "Another copy of this app is already running. Close one -- two will read every "
                + "screenshot twice and fight over removing them.";
        }
    }

    // ---- Map --------------------------------------------------------------

    private async void OnMapSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressMapSelectorEvent || MapSelector.SelectedItem is not GameMap map)
            return;

        // Re-selecting the current map is not a change. Treating it as one reloads the map and
        // wipes the floor selection, and a ComboBox will raise this event on its own when its
        // template is reapplied, e.g. when the window is activated.
        if (ReferenceEquals(map, _session.CurrentMap))
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
        FloorOverlay.IsVisible = map.Floors.Count > 0;

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

        // The collapsed pill has to carry the state, or a map showing an underground level with the
        // panel shut looks like the artwork is simply wrong.
        var shown = _canvas.ActiveFloors.Count + (_canvas.ShowBaseLayer ? 1 : 0);
        FloorOverlayToggle.Content = shown switch
        {
            0 => "Levels · none",
            1 when _canvas.ShowBaseLayer => "Levels",
            1 => $"Levels · {_canvas.ActiveFloors.First()}",
            _ => $"Levels · {shown}",
        };

        // Blank map is worth reading even when the panel is collapsed and translucent.
        FloorOverlay.Classes.Set("attention", nothingShown);
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
        if (FocusToggle.IsChecked.GetValueOrDefault() && _session.ExtractLine.GuideBase is not null)
            ApplyExtractFocus();
        else if (FollowToggle.IsChecked.GetValueOrDefault() && _canvas.Map is { } map)
            _canvas.CenterOn(map.ToBase(fix.Position));

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

    /// <summary>
    /// Reacts to the game's log naming the map it is loading.
    /// </summary>
    /// <remarks>
    /// Deliberately not routed through <see cref="ShowSuggestion"/>, even though the offer path
    /// ends up in the same bar. This is not a guess, so it says so, and it obeys its own preference
    /// rather than the one governing coordinate-based guesses.
    /// </remarks>
    private void OnMapDetectedFromLog(GameMap map)
    {
        if (ReferenceEquals(map, _session.CurrentMap))
        {
            HideSuggestion();
            return;
        }

        if (_settings.AutoSwitchMapFromGameLog)
        {
            HideSuggestion();
            StatusText.Text = $"The game is loading {map.DisplayName}.";

            // Through the selector rather than SetMapAsync directly, so the combo box, the floor
            // list and the extract list all follow along exactly as they do for a manual change.
            MapSelector.SelectedItem = map;
            return;
        }

        _suggestedMap = map;
        SuggestionText.Text = $"The game is loading {map.DisplayName}.";
        SuggestionBar.IsVisible = true;
    }

    private void OnRaidStateChanged(bool started)
    {
        StatusText.Text = started
            ? "Raid started; cleared the previous trail."
            : "Back at the menu.";

        _canvas.InvalidateVisual();
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
