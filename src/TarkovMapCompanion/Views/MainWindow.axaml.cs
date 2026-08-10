using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TarkovMapCompanion.GameLog;
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

    /// <summary>True while a selection change is being handled; see RefreshExtractRows.</summary>
    private bool _inSelectionChange;
    private bool _rowRefreshQueued;
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

        ClearSelectionButton.Click += (_, _) =>
        {
            // Through the list, not the session, so the row highlight lets go with it. Setting the
            // session's selection alone leaves the list looking like something is still chosen.
            ExtractList.SelectedItem = null;
            StatusText.Text = "Exit cleared.";
        };
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

        WireQuests();
        WireNotes();
        WireSidebar();
        WireQuestPane();

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
            UpdateClockSuspension();
        });

        _minimap.Show();

        SyncMinimapLayers();

        // Opening it may be what makes the animation clock worth running again.
        UpdateClockSuspension();
    }

    /// <summary>
    /// Stops the animation clock when there is no window left to see it.
    /// </summary>
    /// <remarks>
    /// The minimap has to be part of this decision, and leaving it out was a real bug rather than a
    /// nicety: the intended way to use that window is main window minimized, small map over the
    /// game. Keying suspension to the main window alone froze the route arrows in the one
    /// configuration the feature exists for.
    /// </remarks>
    private void UpdateClockSuspension()
    {
        if (_mapClock is null)
            return;

        _mapClock.Suspended = WindowState == WindowState.Minimized && _minimap is null;
        _mapClock.Wake();
    }

    /// <summary>Escape leaves marker mode, which is easy to forget you are in.</summary>
    private void Escape() => KeyDown += (_, e) =>
    {
        if (e.Key is not Avalonia.Input.Key.Escape)
            return;

        if (MarkToggle.IsChecked == true)
            MarkToggle.IsChecked = false;

        if (AnnotateToggle.IsChecked == true)
            AnnotateToggle.IsChecked = false;
        else
            CloseAnnotationEditor();

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

        // The "this map" filter and the "on this map" note on every row both depend on which map is
        // shown, so the whole list is rebuilt rather than just the overlay.
        _session.QuestsChanged += (_, _) => Post(() =>
        {
            BuildQuestList();
            BuildQuestPane();
            _canvas.InvalidateVisual();
        });

        _session.AnnotationsChanged += (_, _) => Post(() =>
        {
            BuildNotesList();
            _canvas.InvalidateVisual();
            _minimap?.Redraw();
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

        // Both of these used to have their own control in the panel and now only live in the
        // dialog, so this is the only place they can take effect.
        ApplyExitFilter();
        BuildQuestList();

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

            // The minimap draws the same fading markers and has no clock of its own.
            _minimap?.Redraw();
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
            if (e.Property == WindowStateProperty)
                UpdateClockSuspension();
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

        return new PartyRow(peer.Name, detail, swatch, LatencyLabel(peer));
    }

    /// <summary>
    /// The round trip to show against a roster row.
    /// </summary>
    /// <remarks>
    /// Always latency to the host, which is the only link that exists in a star topology. As a
    /// guest, our own row is the one we measure ourselves, so it uses the local reading rather than
    /// the host's opinion of it. The host's own row shows nothing: it is not going over a network.
    /// </remarks>
    private string LatencyLabel(PartyPeer peer)
    {
        var ms = peer.IsSelf && _session.Party.State == PartyState.Joined
            ? _session.Party.HostLatencyMs
            : peer.LatencyMs;

        return ms is { } value ? $"{value} ms" : "";
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

                // Ticking an exit layer by hand contradicts a narrowed filter, so drop back to
                // "All" rather than leaving the two showing different things. The control that set
                // it is in Settings now, which is all the more reason not to leave it winning
                // silently over something ticked here.
                if (ExtractKinds.Contains(captured) && !_settings.ExitFilter.Includes(captured) && on)
                    _settings.ExitFilter = ExitFilter.All;

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

        _inSelectionChange = true;

        try
        {
            _session.SelectedExtract = ExtractList.SelectedItem as MapPoi;
            UpdateExtractDetail();
            ApplyExtractFocus();
            _canvas.InvalidateVisual();
        }
        finally
        {
            _inSelectionChange = false;
        }
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

        // Only offered when there is something to clear, so it is never a button that does nothing.
        ClearSelectionButton.IsVisible = selected is not null;

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

        // Never while a selection change is being raised. Avalonia's selection model is mid-update
        // at that moment when the assignment came from code rather than a click, and replacing the
        // items under it throws "Cannot change source while update is in progress" -- unhandled, on
        // the UI thread, so it closes the app rather than misbehaving.
        //
        // Waiting for the update to finish is invisible and covers every caller, which matters:
        // clearing the selection from a button is the one that crashed, but selecting an exit by
        // clicking its marker on the map assigns it exactly the same way.
        if (_inSelectionChange)
        {
            if (_rowRefreshQueued)
                return;

            _rowRefreshQueued = true;

            Dispatcher.UIThread.Post(() =>
            {
                _rowRefreshQueued = false;
                RefreshExtractRows();
            });

            return;
        }

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
        var screen = position is { } p ? _canvas.Viewport.ToScreen(p) : (MapPoint?)null;

        var hovered = screen is { } s ? _session.Pois.HitTest(_canvas.Viewport, s.X, s.Y) : null;

        // Quest marks sit above the POI layers and are checked first, so a quest marker on top of a
        // loot container is the one that answers.
        var quest = screen is { } q ? _session.Quests.HitTest(_canvas.Viewport, q.X, q.Y) : null;
        var note = screen is { } n ? _session.Annotations.HitTest(_canvas.Viewport, n.X, n.Y) : null;

        if (ReferenceEquals(hovered, _session.Pois.Hovered)
            && ReferenceEquals(quest, _session.Quests.Hovered)
            && ReferenceEquals(note, _session.Annotations.Hovered))
        {
            return;
        }

        _session.Pois.Hovered = hovered;
        _session.Quests.Hovered = quest;
        _session.Annotations.Hovered = note;

        // A compact tooltip beats a panel round-trip for something that changes on every mouse move.
        var tip = quest is not null
            ? DescribeForTooltip(quest)
            : hovered is null ? null : DescribeForTooltip(hovered);

        _canvas.SetValue(ToolTip.TipProperty, tip);
        _canvas.InvalidateVisual();
    }

    private string DescribeForTooltip(QuestMark mark)
    {
        var lines = new List<string> { mark.TaskName };

        if (!string.IsNullOrWhiteSpace(mark.Description))
            lines.Add(mark.Description);

        if (mark.OneOf)
            lines.Add("One of several places it can be");

        if (_session.Player.Current is { } fix)
            lines.Add($"{fix.Position.GroundDistanceTo(mark.Position):F0} m away");

        lines.Add("Click to open this quest");
        return string.Join(Environment.NewLine, lines);
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

        // Same rule as marker mode: while armed, a click is a note and nothing else.
        if (AnnotateToggle.IsChecked == true)
        {
            BeginAnnotation(position);
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

        // Your own note opens for renaming. A teammate's does not: it is replaced wholesale the
        // next time they publish, so an edit here would be undone within seconds.
        if (_session.Annotations.HitTest(_canvas.Viewport, screen.X, screen.Y) is { Author: null } note)
        {
            BeginRename(note);
            return;
        }

        // A quest marker is a small, deliberate target and the tooltip says what clicking it does,
        // so this is not a click anybody lands on by accident. Undo marker takes it back.
        // Opens it rather than routing to it. A single click quietly adding waypoints was the sort
        // of thing you only notice afterward, and the pane it opens has the route button in it.
        if (_session.Quests.HitTest(_canvas.Viewport, screen.X, screen.Y) is { } mark)
        {
            OpenQuest(mark.TaskId);
            return;
        }

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
        // From the session's own list rather than named one by one here. The two had already
        // drifted once: the minimap loops over this list and got a new overlay for free, while
        // this window quietly did not draw it.
        foreach (var overlay in _session.Overlays)
            _canvas.AddOverlay(overlay);

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

        // A map change resets the canvas's own floor state, so the minimap has to be told again.
        SyncMinimapLayers();

        _canvas.InvalidateVisual();
    }

    /// <summary>
    /// Tells the minimap which floors to draw.
    /// </summary>
    /// <remarks>
    /// The minimap should be the same picture at a different size, and floors were the one place it
    /// was not: it forced the ground level on, so standing in Factory's tunnels showed you on the
    /// big map and drew you over solid concrete on the small one. This state lives on the main
    /// window's canvas and has no event to subscribe to, so it is pushed rather than pulled.
    /// </remarks>
    private void SyncMinimapLayers() =>
        _minimap?.MirrorLayers(_canvas.ActiveFloors, _canvas.ShowBaseLayer);

    // ---- Side panel -------------------------------------------------------

    /// <summary>Width to come back to when the panel is unfolded.</summary>
    private double _sidebarRestoreWidth = 290.0;

    private ColumnDefinition SidebarColumn => ContentGrid.ColumnDefinitions[4];

    private ColumnDefinition QuestPaneColumn => ContentGrid.ColumnDefinitions[0];

    private void WireSidebar()
    {
        ApplySidebarWidth(_settings.SidebarWidth);

        // Dragging is the resize; the width is only worth saving once the drag has finished, not
        // on every pixel of it.
        PanelSplitter.DragCompleted += (_, _) => Apply(() =>
        {
            _settings.SidebarWidth = SidebarColumn.Width.Value;
            _sidebarRestoreWidth = _settings.SidebarWidth;
        });

        // Double-click the divider does the same thing, but a gesture cannot be the only way back
        // from a panel that is no longer on screen. The button is the answer to "where did it go".
        PanelSplitter.DoubleTapped += (_, e) =>
        {
            PanelToggle.IsChecked = !SidePanelHost.IsVisible;
            e.Handled = true;
        };

        PanelToggle.IsChecked = _settings.SidebarWidth > 0;
        PanelToggle.IsCheckedChanged += (_, _) => ShowSidebar(PanelToggle.IsChecked ?? true);
    }

    /// <summary>Folds the panel away, or brings it back to the width it had.</summary>
    private void ShowSidebar(bool shown)
    {
        if (shown == SidePanelHost.IsVisible)
            return;

        Apply(() =>
        {
            ApplySidebarWidth(shown ? _sidebarRestoreWidth : 0);
            _settings.SidebarWidth = shown ? _sidebarRestoreWidth : 0;
        });
    }

    private void ApplySidebarWidth(double width)
    {
        if (width > 0)
            _sidebarRestoreWidth = Math.Clamp(width, 220.0, 700.0);

        var shown = width > 0;

        // The border is hidden as well as the column zeroed, so its own left edge does not leave a
        // stray line down the map when there is nothing behind it.
        SidePanelHost.IsVisible = shown;
        SidebarColumn.Width = new GridLength(shown ? _sidebarRestoreWidth : 0);

        // Without this the column refuses to go below its own minimum and the panel never folds.
        SidebarColumn.MinWidth = shown ? 220 : 0;

        // Folded away, the divider is the only way back, so it stops being a hairline and starts
        // being a handle. A five-pixel strip in the border color at the edge of the window is not
        // something anybody would guess was clickable.
        PanelSplitter.Width = shown ? 5 : 12;
        PanelSplitter.Background = this.FindResource(shown ? "BorderSubtleBrush" : "AccentBrush") as IBrush;

        PanelSplitter.SetValue(
            ToolTip.TipProperty,
            shown
                ? "Drag to resize the panel, double-click to hide it"
                : "Double-click to bring the panel back");
    }

    // ---- Quest detail -----------------------------------------------------

    /// <summary>The task the left pane is showing, or null when it is closed.</summary>
    private string? _openQuestId;

    private double _questPaneRestoreWidth = 360.0;

    private void WireQuestPane()
    {
        QuestPaneCloseButton.Click += (_, _) => CloseQuestPane();

        QuestPaneSplitter.DragCompleted += (_, _) => Apply(() =>
        {
            _questPaneRestoreWidth = Math.Clamp(QuestPaneColumn.Width.Value, 260.0, 700.0);
            _settings.QuestPaneWidth = _questPaneRestoreWidth;
        });

        _questPaneRestoreWidth = Math.Clamp(_settings.QuestPaneWidth, 260.0, 700.0);
    }

    /// <summary>Shows one quest in the left pane, opening it if it was closed.</summary>
    private void OpenQuest(string taskId)
    {
        _openQuestId = taskId;

        QuestPaneHost.IsVisible = true;
        QuestPaneSplitter.IsVisible = true;
        QuestPaneColumn.Width = new GridLength(_questPaneRestoreWidth);

        BuildQuestPane();
    }

    private void CloseQuestPane()
    {
        _openQuestId = null;

        QuestPaneHost.IsVisible = false;
        QuestPaneSplitter.IsVisible = false;
        QuestPaneColumn.Width = new GridLength(0);
    }

    /// <summary>
    /// Lays out everything known about the open quest.
    /// </summary>
    /// <remarks>
    /// The point of this pane is legibility, so the sizes here are deliberate: objective text at 13
    /// rather than the 10 it had in the list, real spacing between objectives, and one idea per
    /// line. The list is for finding a quest; this is for reading one.
    /// </remarks>
    private void BuildQuestPane()
    {
        QuestPane.Children.Clear();

        if (_openQuestId is null || _session.Tasks.Find(_openQuestId) is not { } task)
        {
            CloseQuestPane();
            return;
        }

        QuestPane.Children.Add(new TextBlock
        {
            Text = task.Name,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 2),
        });

        var facts = new List<string> { task.Trader };

        if (task.MinPlayerLevel > 0)
            facts.Add($"level {task.MinPlayerLevel}");

        if (task.KappaRequired)
            facts.Add("Kappa");

        if (task.LightkeeperRequired)
            facts.Add("Lightkeeper");

        if (task.Faction is { Length: > 0 } faction)
            facts.Add(faction);

        QuestPane.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", facts),
            Classes = { "secondary" },
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        QuestPane.Children.Add(BuildQuestPaneActions(task));

        // Prerequisites. The plan called for these and they were missed the first time: a level 40
        // follow-up sat next to the quest that unlocks it with nothing to say which came first.
        if (task.Requires.Count > 0)
            QuestPane.Children.Add(BuildPrerequisites(task));

        if (task.Keys.Count > 0)
            QuestPane.Children.Add(BuildKeys(task));

        QuestPane.Children.Add(Heading("OBJECTIVES", 12));

        var here = 0;

        foreach (var objective in task.Objectives)
        {
            var onThisMap = objective.Points.Count(p => _session.IsOnCurrentMap(p.MapId));
            here += onThisMap;

            QuestPane.Children.Add(BuildObjective(task, objective, onThisMap));
        }

        if (task.Objectives.Count == 0)
        {
            QuestPane.Children.Add(new TextBlock
            {
                Text = "No objectives listed for this task.",
                Classes = { "secondary" },
                FontSize = 12,
            });
        }

        var footer = new StackPanel { Spacing = 4, Margin = new Thickness(0, 10, 0, 0) };

        footer.Children.Add(new TextBlock
        {
            Text = here == 0
                ? $"Nothing from this task is on {_session.CurrentMap.DisplayName}."
                : $"{here} place{(here == 1 ? "" : "s")} on {_session.CurrentMap.DisplayName}.",
            Classes = { "secondary" },
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });

        var ticked = task.Objectives.Count(o => _session.IsObjectiveDone(o.Id));

        if (ticked > 0)
        {
            footer.Children.Add(new TextBlock
            {
                Text = $"{ticked} of {task.Objectives.Count} marked done.",
                Classes = { "secondary" },
                FontSize = 11,
            });

            var reset = new Button
            {
                Content = "Clear the ticks",
                FontSize = 11,
                Padding = new Thickness(8, 2),
                MinHeight = 0,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                [ToolTip.TipProperty] = "Un-mark every objective of this task, for a new wipe or a repeatable",
            };

            reset.Click += (_, _) => Apply(() =>
            {
                _session.ClearObjectivesDone(task);
                _canvas.InvalidateVisual();
                BuildQuestPane();
            });

            footer.Children.Add(reset);
        }

        QuestPane.Children.Add(footer);
    }

    private Control BuildQuestPaneActions(Data.Models.TaskData task)
    {
        var row = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 10) };

        var track = new CheckBox
        {
            Content = "Track on the map",
            IsChecked = _session.IsTracked(task.Id),
            FontSize = 12,
        };

        track.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _session.SetTracked(task.Id, track.IsChecked ?? false);
            _canvas.InvalidateVisual();
        });

        row.Children.Add(track);

        // What the game itself says, which is not the same thing as whether it is being drawn.
        if (_session.QuestProgressFromLog.TryGetValue(task.Id, out var progress))
        {
            row.Children.Add(new TextBlock
            {
                Text = progress switch
                {
                    QuestProgress.Active => "The game says you have this one accepted.",
                    QuestProgress.Completed => "The game says you have handed this one in.",
                    QuestProgress.Failed => "The game says this one was failed.",
                    _ => "",
                },
                Classes = { "secondary" },
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };

        var route = new Button
        {
            Content = "Add to route",
            FontSize = 11,
            Padding = new Thickness(8, 3),
            MinHeight = 0,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            [ToolTip.TipProperty] = "Add every objective of this task that is on this map",
        };

        route.Click += (_, _) => AddTaskToRoute(task);
        Grid.SetColumn(route, 0);
        buttons.Children.Add(route);

        if (task.WikiLink is { Length: > 0 } wiki)
        {
            var link = new Button
            {
                Content = "Wiki",
                FontSize = 11,
                Padding = new Thickness(8, 3),
                MinHeight = 0,
            };

            link.Click += (_, _) => OpenUrl(wiki);
            Grid.SetColumn(link, 1);
            buttons.Children.Add(link);
        }

        row.Children.Add(buttons);
        return row;
    }

    private Control BuildPrerequisites(Data.Models.TaskData task)
    {
        var block = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 10) };
        block.Children.Add(Heading("NEEDS FIRST", 12));

        foreach (var id in task.Requires)
        {
            var required = _session.Tasks.Find(id);
            var done = _session.QuestProgressFromLog.TryGetValue(id, out var p) && p == QuestProgress.Completed;

            block.Children.Add(new TextBlock
            {
                Text = (done ? "✓ " : "· ") + (required?.Name ?? "a task this build does not know"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,

                // Dimmed rather than hidden when it is still outstanding, which is the habit the
                // rest of the app already has with exits it cannot confirm.
                Opacity = done ? 0.6 : 1.0,
            });
        }

        return block;
    }

    /// <summary>
    /// The keys this task needs, grouped by the map they open something on.
    /// </summary>
    /// <remarks>
    /// Upstream scopes keys per map, so this is its grouping rather than one invented here. The
    /// current map sorts first and says so; a key for somewhere else is still worth knowing about
    /// before you commit to the task, but it is not what you are packing for right now.
    /// </remarks>
    private Control BuildKeys(Data.Models.TaskData task)
    {
        var block = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 10) };
        block.Children.Add(Heading("KEYS", 12));

        foreach (var group in task.Keys.OrderByDescending(k => _session.IsOnCurrentMap(k.MapId)))
        {
            var here = _session.IsOnCurrentMap(group.MapId);
            var where = here ? "here" : _session.MapNameFor(group.MapId) ?? "a map this build does not know";

            // Keys carry no quantity: one key opens the door however many tasks are behind it.
            var chips = ItemChips(group.Keys.Select(k => new Data.TaskItemNeed(k, 1)), 12);

            // Same habit as the prerequisites: the one that matters on this map is the one at full
            // strength, and the rest are dimmed rather than dropped.
            chips.Opacity = here ? 1.0 : 0.65;

            block.Children.Add(chips);
            block.Children.Add(new TextBlock
            {
                Text = where,
                Classes = { "secondary" },
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 4),
                Opacity = here ? 1.0 : 0.65,
            });
        }

        return block;
    }

    /// <summary>
    /// A run of items, each with its picture where one can be had.
    /// </summary>
    /// <remarks>
    /// A wrap panel rather than a joined string: an icon is what makes "Portable bunkhouse key"
    /// findable in a stash of forty keys, and reading a comma-separated line of them never was.
    /// The name stays regardless, so an icon that will not load costs nothing.
    /// </remarks>
    private Control ItemChips(IEnumerable<Data.TaskItemNeed> needs, double fontSize)
    {
        var panel = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };

        foreach (var need in needs)
        {
            var item = need.Item;

            var chip = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(0, 1, 10, 1),
                [ToolTip.TipProperty] = item.Name,
            };

            var icon = new Image
            {
                Width = 22,
                Height = 22,
                Stretch = Stretch.Uniform,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            LoadIcon(icon, item.Id);

            chip.Children.Add(icon);
            chip.Children.Add(new TextBlock
            {
                Text = need.Label,
                FontSize = fontSize,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });

            panel.Children.Add(chip);
        }

        return panel;
    }

    /// <summary>
    /// Fills in an item's picture once it arrives, and leaves the space empty if it never does.
    /// </summary>
    /// <remarks>
    /// Fire and forget on purpose. The panel is built synchronously because the rest of it is
    /// worth reading now, and an icon is worth exactly as much whenever it turns up.
    /// </remarks>
    private async void LoadIcon(Image image, string id)
    {
        try
        {
            if (await _session.Icons.GetAsync(id) is not { } bytes)
                return;

            using var stream = new MemoryStream(bytes);
            image.Source = new Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch (Exception ex)
        {
            // Anything at all: a picture is the most disposable thing in this window.
            Log.Warn($"[icons] could not show {id}: {ex.Message}");
        }
    }

    private Control BuildObjective(Data.Models.TaskData task, Data.Models.TaskObjectiveData objective, int onThisMap)
    {
        var block = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 9) };

        var done = _session.IsObjectiveDone(objective.Id);

        // The tick is the objective's own, not the task's: half the reason to read a quest is to
        // work out which two of its nine parts are left. Nothing in any log says this, so it is a
        // note to yourself, and it persists as one.
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6 };

        var tick = new CheckBox
        {
            IsChecked = done,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            [ToolTip.TipProperty] = "Mark this one done. It stays on the map, faded.",
        };

        var text = new TextBlock
        {
            Text = objective.Description,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Opacity = done ? 0.5 : objective.Optional ? 0.7 : 1.0,
            TextDecorations = done ? Avalonia.Media.TextDecorations.Strikethrough : null,
        };

        tick.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _session.SetObjectiveDone(objective.Id, tick.IsChecked ?? false);
            _canvas.InvalidateVisual();
            BuildQuestPane();
        });

        Grid.SetColumn(tick, 0);
        Grid.SetColumn(text, 1);
        header.Children.Add(tick);
        header.Children.Add(text);

        block.Children.Add(header);

        // The items by name, under the objective that wants them. The description usually names
        // one of them in passing; this is the list, including the alternatives it does not mention
        // — "Obtain the item: Rye croutons" also accepts Emelya rye croutons, and only this says so.
        if (objective.Items.Count > 0)
        {
            var carried = Data.TaskRequirements.CarriedTypes.Contains(objective.Type);

            block.Children.Add(new TextBlock
            {
                Text = carried ? "Bring" : "Items",
                Classes = { "heading" },
                FontSize = 10,
                Margin = new Thickness(0, 3, 0, 1),
            });

            var wanted = Math.Max(1, objective.Count ?? 1);

            var chips = ItemChips(objective.Items.Select(i => new Data.TaskItemNeed(i, wanted)), 12);
            chips.Opacity = objective.Optional ? 0.7 : 1.0;
            block.Children.Add(chips);
        }

        var notes = new List<string>();

        if (done)
            notes.Add("done");

        if (objective.Optional)
            notes.Add("optional");

        if (objective.Count is > 1)
            notes.Add($"{objective.Count} needed");

        if (objective.FoundInRaid)
            notes.Add("found in raid");

        notes.Add(onThisMap > 0
            ? $"{onThisMap} on {_session.CurrentMap.DisplayName}"
            : objective.Points.Count > 0 ? "on another map" : "nowhere in particular");

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };

        var meta = new TextBlock
        {
            Text = string.Join(" · ", notes),
            Classes = { "secondary" },
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        Grid.SetColumn(meta, 0);
        footer.Children.Add(meta);

        // Per objective, not just per task: half the value of a long quest is going to one part of
        // it, and adding all eleven of Urban Medicine's places is rarely what anybody wants.
        if (onThisMap > 0 && !done && _session.IsTracked(task.Id))
        {
            var route = new Button
            {
                Content = "Route",
                FontSize = 10,
                Padding = new Thickness(6, 1),
                MinHeight = 0,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                [ToolTip.TipProperty] = "Add just this objective to your route",
            };

            route.Click += (_, _) => AddObjectiveToRoute(task, objective);

            Grid.SetColumn(route, 1);
            footer.Children.Add(route);
        }

        block.Children.Add(footer);
        return block;
    }

    private void AddObjectiveToRoute(Data.Models.TaskData task, Data.Models.TaskObjectiveData objective)
    {
        var marks = _session.Quests.Marks
            .Where(m => string.Equals(m.ObjectiveId, objective.Id, StringComparison.Ordinal))
            .ToArray();


        foreach (var mark in marks)
            AddQuestToRoute(mark, quiet: true);

        StatusText.Text = marks.Length == 0
            ? "Nothing from that objective is on this map."
            : $"Added {marks.Length} place{(marks.Length == 1 ? "" : "s")} from {task.Name}.";
    }

    private static TextBlock Heading(string text, double size) => new()
    {
        Text = text,
        Classes = { "heading" },
        FontSize = size,
        Margin = new Thickness(0, 0, 0, 4),
    };

    // ---- Notes ------------------------------------------------------------

    /// <summary>Where the note being typed will land, in base pixels. Null when not placing one.</summary>
    private MapPoint? _annotationAt;

    /// <summary>The note being renamed, when the editor was opened by clicking an existing one.</summary>
    private string? _annotationEditingId;

    private void WireNotes()
    {
        ShowAnnotationsBox.IsChecked = _settings.ShowAnnotations;
        ShareAnnotationsBox.IsChecked = _settings.ShareAnnotationsWithParty;
        NotesPath.Text = _session.Notes.FilePath;

        AnnotateToggle.IsCheckedChanged += (_, _) => OnAnnotateToggled();

        ShowAnnotationsBox.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _settings.ShowAnnotations = ShowAnnotationsBox.IsChecked ?? true;
            _session.Annotations.IsVisible = _settings.ShowAnnotations;
            _canvas.InvalidateVisual();
        });

        ShareAnnotationsBox.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _settings.ShareAnnotationsWithParty = ShareAnnotationsBox.IsChecked ?? false;

            // Turning it off has to withdraw what is already out there, not merely stop sending.
            _session.RepublishAnnotations();
        });

        AnnotationSaveButton.Click += (_, _) => CommitAnnotation();
        AnnotationCancelButton.Click += (_, _) => CloseAnnotationEditor();

        AnnotationDeleteButton.Click += (_, _) =>
        {
            if (_annotationEditingId is { } id && _session.Notes.Remove(id))
                StatusText.Text = "Note removed.";

            CloseAnnotationEditor();
        };

        AnnotationTextBox.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Avalonia.Input.Key.Enter:
                    CommitAnnotation();
                    e.Handled = true;
                    break;

                case Avalonia.Input.Key.Escape:
                    CloseAnnotationEditor();
                    e.Handled = true;
                    break;
            }
        };

        ImportNotesButton.Click += async (_, _) => await ImportNotesAsync();
        ExportNotesButton.Click += async (_, _) => await ExportNotesAsync();

        ClearNotesButton.Click += (_, _) =>
        {
            var removed = _session.Notes.RemoveAllOn(_session.CurrentMap.NormalizedName);

            StatusText.Text = removed == 0
                ? $"No notes on {_session.CurrentMap.DisplayName}."
                : $"Removed {removed} note{(removed == 1 ? "" : "s")} from {_session.CurrentMap.DisplayName}.";
        };

        BuildNotesList();
    }

    private void OnAnnotateToggled()
    {
        var on = AnnotateToggle.IsChecked ?? false;

        // The two placing modes are mutually exclusive: one click cannot be both a route marker
        // and a label, and leaving both armed would make it a coin toss which you got.
        if (on && MarkToggle.IsChecked == true)
            MarkToggle.IsChecked = false;

        _canvas.Cursor = on
            ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Cross)
            : Avalonia.Input.Cursor.Default;

        StatusText.Text = on
            ? "Click the map to write a note there."
            : "";

        if (!on)
            CloseAnnotationEditor();
    }

    /// <summary>Opens the editor for a new note at a spot on the map.</summary>
    private void BeginAnnotation(MapPoint basePoint)
    {
        _annotationAt = basePoint;
        _annotationEditingId = null;

        AnnotationEditorTitle.Text = "LABEL THIS SPOT";
        AnnotationTextBox.Text = "";
        AnnotationDeleteButton.IsVisible = false;
        AnnotationEditor.IsVisible = true;
        AnnotationTextBox.Focus();
    }

    /// <summary>Opens the editor on a note that already exists.</summary>
    private void BeginRename(Data.MapAnnotation annotation)
    {
        _annotationAt = null;
        _annotationEditingId = annotation.Id;

        AnnotationEditorTitle.Text = "EDIT THIS NOTE";
        AnnotationTextBox.Text = annotation.Text;
        AnnotationDeleteButton.IsVisible = true;
        AnnotationEditor.IsVisible = true;
        AnnotationTextBox.Focus();
        AnnotationTextBox.SelectAll();
    }

    private void CommitAnnotation()
    {
        var text = AnnotationTextBox.Text;

        if (_annotationEditingId is { } id)
        {
            if (_session.Notes.Retext(id, text))
                StatusText.Text = "Note renamed.";
        }
        else if (_annotationAt is { } at)
        {
            if (_session.AddAnnotation(at, text) is not null)
                StatusText.Text = $"Note added to {_session.CurrentMap.DisplayName}.";
            else
                StatusText.Text = $"A map holds at most {Data.AnnotationStore.MaxPerMap} notes.";
        }

        CloseAnnotationEditor();
    }

    private void CloseAnnotationEditor()
    {
        AnnotationEditor.IsVisible = false;
        _annotationAt = null;
        _annotationEditingId = null;
    }

    private void BuildNotesList()
    {
        NotesList.Children.Clear();

        var here = _session.Notes.ForMap(_session.CurrentMap.NormalizedName);

        foreach (var annotation in here.OrderBy(a => a.Text, StringComparer.OrdinalIgnoreCase))
            NotesList.Children.Add(BuildNoteRow(annotation));

        var shared = here.Count(a => a.Author is not null);

        NotesHint.Text = here.Count == 0
            ? $"No notes on {_session.CurrentMap.DisplayName} yet."
            : $"{here.Count} on {_session.CurrentMap.DisplayName}"
              + (shared > 0 ? $", {shared} from the squad." : ".");
    }

    private Control BuildNoteRow(Data.MapAnnotation annotation)
    {
        var mine = annotation.Author is null;

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 6,
            Margin = new Thickness(0, 2, 0, 2),
        };

        var label = new TextBlock
        {
            Text = annotation.Text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,

            // A teammate's note is dimmed, because it is not yours to keep and will go when the
            // session does.
            Opacity = mine ? 1.0 : 0.75,
        };

        if (!mine)
            label.SetValue(ToolTip.TipProperty, $"Shared by {annotation.Author}");

        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        // Only your own can be edited or deleted. Theirs are replaced wholesale every time they
        // publish, so a local edit would be undone within seconds and a delete within one message.
        if (mine)
        {
            var rename = new Button
            {
                Content = "Edit",
                FontSize = 10,
                Padding = new Thickness(6, 1),
                MinHeight = 0,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            rename.Click += (_, _) => BeginRename(annotation);

            var remove = new Button
            {
                Content = "✕",
                FontSize = 10,
                Padding = new Thickness(6, 1),
                MinHeight = 0,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                [ToolTip.TipProperty] = "Delete this note",
            };

            remove.Click += (_, _) => _session.Notes.Remove(annotation.Id);

            Grid.SetColumn(rename, 1);
            Grid.SetColumn(remove, 2);
            row.Children.Add(rename);
            row.Children.Add(remove);
        }
        else
        {
            var who = new TextBlock
            {
                Text = annotation.Author,
                FontSize = 10,
                Classes = { "secondary" },
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            Grid.SetColumn(who, 1);
            row.Children.Add(who);
        }

        return row;
    }

    private async Task ImportNotesAsync()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import map notes",
            AllowMultiple = false,
        });

        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { Length: > 0 } path)
            return;

        try
        {
            var added = _session.Notes.Import(path);

            StatusText.Text = added == 0
                ? "Nothing new in that file; every note in it was already here."
                : $"Imported {added} note{(added == 1 ? "" : "s")}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not import that file: {ex.Message}";
        }
    }

    private async Task ExportNotesAsync()
    {
        var picked = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export map notes",
            SuggestedFileName = "tarkov-map-notes.json",
            DefaultExtension = "json",
        });

        if (picked?.TryGetLocalPath() is not { Length: > 0 } path)
            return;

        try
        {
            var written = _session.Notes.Export(path);
            StatusText.Text = $"Wrote {written} note{(written == 1 ? "" : "s")} to {path}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not write that file: {ex.Message}";
        }
    }

    // ---- Quests -----------------------------------------------------------

    private void WireQuests()
    {
        // What is on the map in front of you, which is a useful first open rather than a wall of
        // five hundred tasks. The level filter stays off until you have told it your level:
        // defaulting it on with the default level of 1 hides all but a handful of tasks, and the
        // panel looks broken rather than filtered.
        QuestThisMapBox.IsChecked = true;
        QuestLevelBox.IsChecked = false;

        foreach (var box in new[] { QuestThisMapBox, QuestTrackedBox, QuestKappaBox, QuestLevelBox, QuestActiveHereBox })
            box.IsCheckedChanged += (_, _) => BuildQuestList();

        QuestSearchBox.TextChanged += (_, _) => BuildQuestList();

        QuestLabelsBox.IsChecked = _settings.ShowQuestNames;
        QuestLabelsBox.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _settings.ShowQuestNames = QuestLabelsBox.IsChecked ?? true;
            _session.Quests.ShowNames = _settings.ShowQuestNames;
            _canvas.InvalidateVisual();
        });

        ClearQuestsButton.Click += (_, _) => Apply(() =>
        {
            _session.ClearTrackedTasks();
            BuildQuestList();
            _canvas.InvalidateVisual();
        });

        SyncQuestsButton.Click += (_, _) => Apply(() =>
        {
            var tracked = _session.SyncTrackedFromQuestLog();

            BuildQuestList();
            _canvas.InvalidateVisual();

            StatusText.Text = tracked == 0
                ? "The log has no quest of yours open. Nothing is tracked now."
                : $"Tracking the {tracked} quest{(tracked == 1 ? "" : "s")} the game has open.";
        });

        // The floor the log implies is known before any of this ran, since the watcher reads the
        // history at startup. Apply it once here so the first list is filtered by the right number
        // rather than by the default of 1.
        _session.ApplyLevelFloorFromQuestLog();
    }

    /// <summary>
    /// Rebuilds the quest list from the filters.
    /// </summary>
    /// <remarks>
    /// Built in code rather than bound to a template, for the same reason the floor list is: it is
    /// a flat panel of grouped rows whose grouping changes with the filters, and an
    /// ItemsControl with a converter for each of the six things a row shows would be more
    /// machinery than the whole panel is worth.
    /// </remarks>
    private void BuildQuestList()
    {
        QuestList.Children.Clear();

        var search = (QuestSearchBox.Text ?? "").Trim();

        // Eight of the 510 tasks share a display name with another one. They are genuinely
        // different tasks upstream and only their normalized name tells them apart, so those rows
        // get it appended rather than sitting there as two identical lines. Computed over every
        // task, not the filtered set, so a name does not stop being ambiguous when you search.
        var ambiguous = _session.Tasks.Tasks
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matching = _session.Tasks.Tasks.Where(Matches).ToArray();

        foreach (var group in matching
                     .GroupBy(t => t.Trader, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            QuestList.Children.Add(new TextBlock
            {
                Text = group.Key.ToUpperInvariant(),
                Classes = { "heading" },
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 2),
            });

            foreach (var task in group.OrderBy(t => t.MinPlayerLevel).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                QuestList.Children.Add(BuildQuestRow(task, ambiguous.Contains(task.Name)));
        }

        UpdateQuestHint(matching.Length);
        return;

        bool Matches(Data.Models.TaskData task)
        {
            // One switch standing in for two, because it is one question. Deliberately not tied to
            // tracking: what the game has open is a fact, and what you have ticked is a choice.
            if (QuestActiveHereBox.IsChecked == true && (!_session.IsActivePerLog(task.Id) || !HasObjectiveHere(task)))
                return false;

            if (QuestTrackedBox.IsChecked == true && !_session.IsTracked(task.Id))
                return false;

            if (QuestKappaBox.IsChecked == true && !task.KappaRequired)
                return false;

            if (QuestLevelBox.IsChecked == true && task.MinPlayerLevel > _settings.PlayerLevel)
                return false;

            if (QuestThisMapBox.IsChecked == true && !HasObjectiveHere(task))
                return false;

            if (search.Length == 0)
                return true;

            return task.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                   || task.Trader.Contains(search, StringComparison.OrdinalIgnoreCase)
                   || task.Objectives.Any(o => o.Description.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
    }

    private bool HasObjectiveHere(Data.Models.TaskData task) =>
        task.Objectives.Any(o => o.Points.Any(p => _session.IsOnCurrentMap(p.MapId)));

    private Control BuildQuestRow(Data.Models.TaskData task, bool ambiguous)
    {
        var here = HasObjectiveHere(task);

        // The tick and the name do different things now, so they are different controls. Putting
        // the name inside the checkbox made reading a quest and tracking it the same click.
        var tick = new CheckBox
        {
            IsChecked = _session.IsTracked(task.Id),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            [ToolTip.TipProperty] = "Draw this task's objectives on the map",
        };

        var title = new TextBlock
        {
            Text = task.Name,
            Classes = { "link" },
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            [ToolTip.TipProperty] = "Click the name to read this task: objectives, keys and items",
        };

        title.PointerPressed += (_, _) => OpenQuest(task.Id);

        tick.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _session.SetTracked(task.Id, tick.IsChecked ?? false);
            UpdateQuestHint(null);
            _canvas.InvalidateVisual();
        });

        var notes = new List<string>();

        // Most tasks have no level requirement at all, and "level 0" reads as a fact rather than
        // as the absence of one.
        if (task.MinPlayerLevel > 0)
            notes.Add($"level {task.MinPlayerLevel}");

        if (ambiguous)
            notes.Add(task.NormalizedName);

        if (task.KappaRequired)
            notes.Add("Kappa");

        if (task.LightkeeperRequired)
            notes.Add("Lightkeeper");

        if (task.Faction is { Length: > 0 } faction)
            notes.Add(faction);

        // Said plainly rather than by omission. A ticked task whose objectives are all somewhere
        // else draws nothing, and without this the panel looks broken rather than correct.
        notes.Add(here ? "on this map" : "not on this map");

        var subtitle = new TextBlock
        {
            Text = string.Join(" · ", notes),
            Classes = { "secondary" },
            FontSize = 10,
            Margin = new Thickness(26, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var row = new StackPanel { Spacing = 1, Margin = new Thickness(0, 2, 0, 2) };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        Grid.SetColumn(tick, 0);
        Grid.SetColumn(title, 1);
        header.Children.Add(tick);
        header.Children.Add(title);

        if (here)
        {
            var route = new Button
            {
                Content = "Route",
                FontSize = 10,
                Padding = new Thickness(6, 1),
                MinHeight = 0,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                [ToolTip.TipProperty] = "Add this task's objectives on this map to your route",
            };

            route.Click += (_, _) => AddTaskToRoute(task);

            Grid.SetColumn(route, 2);
            header.Children.Add(route);
        }

        if (task.WikiLink is { Length: > 0 } wiki)
        {
            var link = new Button
            {
                Content = "?",
                FontSize = 10,
                Padding = new Thickness(6, 1),
                MinHeight = 0,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                [ToolTip.TipProperty] = "Open the wiki page for this task",
            };

            link.Click += (_, _) => OpenUrl(wiki);

            Grid.SetColumn(link, 3);
            header.Children.Add(link);
        }

        row.Children.Add(header);
        row.Children.Add(subtitle);

        // Objective text used to expand inline here for every ticked task, which turned a list you
        // scan into a wall you scroll. It lives in the reading pane now, one click away on the
        // name, at a size you can actually read.
        return row;
    }

    private void AddTaskToRoute(Data.Models.TaskData task)
    {
        // Ticked-off objectives are already gone from the map, so this is only ever the parts
        // still outstanding.
        var added = _session.Quests.Marks
            .Where(m => string.Equals(m.TaskId, task.Id, StringComparison.Ordinal))
            .ToArray();

        if (added.Length == 0)
        {
            // Distinguishing "you have done all of these" from "this task is not tracked" needs the
            // task's own objectives, since neither case leaves anything on the map to count.
            var hereAndDone = task.Objectives.Any(o =>
                _session.IsObjectiveDone(o.Id) && o.Points.Any(p => _session.IsOnCurrentMap(p.MapId)));

            StatusText.Text = hereAndDone
                ? $"Every part of {task.Name} on this map is already marked done."

                // Only tracked tasks have marks, so this is the "ticked it and pressed Route in one
                // motion" case rather than an error.
                : $"Tick {task.Name} first, then add it to your route.";

            return;
        }

        foreach (var mark in added)
            AddQuestToRoute(mark, quiet: true);

        StatusText.Text = $"Added {added.Length} objective{(added.Length == 1 ? "" : "s")} from {task.Name} to your route.";
    }

    private void AddQuestToRoute(QuestMark mark, bool quiet = false)
    {
        if (_canvas.Map is not { } map)
            return;

        _session.AddWaypoint(map.ToBase(mark.Position));

        if (!quiet)
            StatusText.Text = $"Added {mark.TaskName} to your route. Undo marker takes it back.";
    }

    private void UpdateQuestHint(int? shown)
    {
        var tracked = _settings.TrackedTasks.Count;
        var drawn = _session.Quests.Marks.Count;

        QuestHint.Text = tracked == 0
            ? "Tick a task to draw it on the map. Click its name to read it."
            : $"{tracked} tracked, {drawn} objective{(drawn == 1 ? "" : "s")} on {_session.CurrentMap.DisplayName}."
              + " Click a name to read the task.";

        BuildQuestKit();

        if (shown is { } count)
        {
            var origin = $"{count} of {_session.Tasks.Tasks.Count} tasks shown · data from {_session.Tasks.Origin}";

            // Only when there is a choice to be confused about. On a one-character account this
            // would be a hex string in the corner saying nothing.
            if (_session.QuestProfileCount > 1 && _session.QuestProfile is { Length: > 8 } profile)
                origin += $" · following character {profile[^8..]} of {_session.QuestProfileCount}";

            QuestOrigin.Text = origin;
        }
    }

    /// <summary>
    /// The "take this with you" block: what the tracked tasks need on the map being shown.
    /// </summary>
    /// <remarks>
    /// Aggregated across tasks rather than per task, because the question it answers is asked once,
    /// at the stash, about the whole trip. Empty means hidden — a box saying "nothing" is worse
    /// than no box.
    /// </remarks>
    private void BuildQuestKit()
    {
        QuestKit.Children.Clear();

        var tracked = _session.Tasks.Tasks.Where(t => _session.IsTracked(t.Id));
        var kit = TaskRequirements.Gather(tracked, _session.IsOnCurrentMap);

        QuestKitHost.IsVisible = !kit.IsEmpty;

        if (kit.IsEmpty)
            return;

        QuestKit.Children.Add(Heading($"TAKE TO {_session.CurrentMap.DisplayName.ToUpperInvariant()}", 11));

        if (kit.Keys.Count > 0)
            AddSection("Keys", [.. kit.Keys.Select(k => new Data.TaskItemNeed(k, 1))]);

        if (kit.Items.Count > 0)
            AddSection("Items", kit.Items);

        void AddSection(string label, IReadOnlyList<Data.TaskItemNeed> items)
        {
            // Capped, because a dozen tracked tasks can name more keys than the sidebar is wide.
            // The count carries the rest rather than the list quietly ending.
            const int Shown = 10;

            QuestKit.Children.Add(new TextBlock
            {
                Text = label,
                Classes = { "heading" },
                FontSize = 10,
                Margin = new Thickness(0, 2, 0, 1),
            });

            QuestKit.Children.Add(ItemChips(items.Take(Shown), 11));

            if (items.Count > Shown)
            {
                QuestKit.Children.Add(new TextBlock
                {
                    Text = $"and {items.Count - Shown} more",
                    Classes = { "secondary" },
                    FontSize = 10,
                });
            }
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn($"could not open {url}: {ex.Message}");
        }
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
            SyncMinimapLayers();
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
                SyncMinimapLayers();
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
