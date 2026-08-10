using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.Views;

/// <summary>
/// A small always-on-top map, meant to sit over the game rather than beside it.
/// </summary>
/// <remarks>
/// <para>
/// The same overlays as the main window, drawn by a second canvas. Overlays already hand out
/// snapshots under a lock because the folder-watcher thread writes them while the render thread
/// reads, so a second reader costs nothing and needs no synchronization of its own.
/// </para>
/// <para>
/// The image source is <em>not</em> shared. SvgMapSource keeps one rasterized snapshot keyed by
/// zoom, so two canvases at different scales would invalidate each other every frame.
/// </para>
/// <para>
/// Nothing here touches the game. It is an ordinary top-level window that the compositor happens to
/// draw above a borderless one, showing the same screenshot-derived information the main window
/// already shows.
/// </para>
/// </remarks>
public partial class MinimapWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MapSession _session;
    private readonly Action _persist;
    private readonly MapCanvas _canvas = new();

    private IMapImageSource? _imageSource;
    private bool _loading = true;
    private bool _loggedTransparency;

    // Parameterless ctor exists only for the XAML previewer.
    public MinimapWindow() : this(new AppSettings(), null!, () => { })
    {
    }

    public MinimapWindow(AppSettings settings, MapSession session, Action persist)
    {
        _settings = settings;
        _session = session;
        _persist = persist;

        InitializeComponent();

        // Index 0, for the same reason as the main window: the resize grip is a XAML child of this
        // Panel, and appending the canvas would bury it.
        MinimapHost.Children.Insert(0, _canvas);

        // The minimap is a view, never an editor. Clicking it should not drop a route marker or
        // select an exit behind your back while you are trying to move the window.
        _canvas.IsHitTestVisible = false;

        OpacitySlider.Value = _settings.MinimapOpacity;

        RestorePlacement();
        WireControls();

        _loading = false;

        Opened += async (_, _) => await StartAsync();
        Closing += (_, _) => Teardown();
    }

    /// <summary>Raised when the window closes, so the main window can un-press its button.</summary>
    public event EventHandler? Dismissed;

    private void WireControls()
    {
        // Dragging anywhere moves the window, not just the strip. The canvas is hit-test invisible,
        // so a press on the map falls through to here -- which is what you want on something this
        // small, where a six-pixel-tall title bar is a poor target and the map is most of the area.
        // Buttons and the slider handle their own presses and never reach this.
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };

        ResizeGrip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginResizeDrag(WindowEdge.SouthEast, e);
        };

        CloseButton.Click += (_, _) => Close();

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;

            Close();
            e.Handled = true;
        };

        ZoomInButton.Click += (_, _) => Rescale(1.0 / 1.4);
        ZoomOutButton.Click += (_, _) => Rescale(1.4);

        // The wheel zooms, which is the first thing anybody tries. Handled on the window rather than
        // the canvas, which is hit-test invisible and would never see it.
        PointerWheelChanged += (_, e) => Rescale(e.Delta.Y > 0 ? 1.0 / 1.2 : 1.2);

        OpacitySlider.PropertyChanged += (_, e) =>
        {
            if (_loading || e.Property != Slider.ValueProperty)
                return;

            _settings.MinimapOpacity = Math.Round(OpacitySlider.Value, 2);
            ApplyOpacity();
            _persist();
        };
    }

    /// <summary>
    /// Fades the map, leaving the header alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two wrong ways to do this were tried first, and both are worth naming. <c>Window.Opacity</c>
    /// fades the whole window, so at the setting that finally lets you see the game through the map
    /// the buttons and the range readout have gone faint with it. Putting the opacity on the panel
    /// holding the canvas fades nothing at all: the map is drawn through a raw Skia lease that
    /// Avalonia's opacity never reaches, so the setting appears to be ignored.
    /// </para>
    /// <para>
    /// What works is the canvas applying the alpha itself. Both the header and the resize grip stay
    /// solid, which is what you want on something sitting over a game.
    /// </para>
    /// </remarks>
    private void ApplyOpacity()
    {
        _canvas.MapOpacity = _settings.MinimapOpacity;
        _canvas.InvalidateVisual();

        // Logged once. Whether the compositor granted a transparent window decides whether the map
        // fades toward the game or toward this window's own background, and the difference is hard
        // to argue about from a screenshot.
        if (_loggedTransparency)
            return;

        _loggedTransparency = true;
        Diagnostics.Log.Info($"[minimap] transparency granted: {ActualTransparencyLevel}");
    }

    private async Task StartAsync()
    {
        ApplyClickThrough();

        // Not in the constructor: ActualTransparencyLevel is whatever the compositor granted, and
        // it is not decided until the window exists.
        ApplyOpacity();

        _session.FixApplied += OnFixApplied;
        _session.MapChanged += OnMapChanged;
        _session.PoisChanged += OnRedrawWanted;
        _session.WaypointsChanged += OnRedrawWanted;
        _session.ExitAvailabilityChanged += OnRedrawWanted;
        _session.QuestsChanged += OnRedrawWanted;

        // A raid starting drops the trail, and choosing an exit moves the guide line. Both are
        // drawn here too.
        _session.RaidStateChanged += OnRaidStateChanged;
        _session.SelectionChanged += OnRedrawWanted;

        // The squad. Sharing the overlays was never enough on its own: they are the same objects
        // the main window draws, but nothing here was listening for them changing, so a teammate
        // moved on the big map and stayed put on this one until the next screenshot happened to
        // repaint it. Everything the main window redraws for, this has to redraw for too.
        _session.Party.Changed += OnRedrawWanted;
        _session.Party.RoutesChanged += OnRedrawWanted;
        _session.PingAdded += OnRedrawWanted;

        // Same overlay instances as the main window, so everything stays in step with no plumbing.
        foreach (var overlay in _session.Overlays)
            _canvas.AddOverlay(overlay);

        await LoadMapAsync();
    }

    private async Task LoadMapAsync()
    {
        MapName.Text = _session.CurrentMap.DisplayName;

        try
        {
            var previous = _imageSource;
            _imageSource = await _session.CreateImageSourceAsync();
            previous?.Dispose();

            _canvas.SetMap(_session.CurrentMap, _imageSource);
            _canvas.ShowBaseLayer = true;

            Recenter();
        }
        catch (Exception ex)
        {
            // A minimap that cannot draw its map is a nuisance; one that takes the app with it is a
            // bug report.
            Diagnostics.Log.Warn($"minimap could not load {_session.CurrentMap.NormalizedName}: {ex.Message}");
        }
    }

    private void OnRedrawWanted(object? sender, object? _) => Dispatcher.UIThread.Post(_canvas.InvalidateVisual);

    // Its own method only because bool is a value type, so it will not bind to the shared handler
    // the way the reference-typed event payloads do.
    private void OnRaidStateChanged(object? sender, bool started) => OnRedrawWanted(sender, null);

    private void OnMapChanged(object? sender, GameMap map) =>
        Dispatcher.UIThread.Post(async void () =>
        {
            try
            {
                await LoadMapAsync();
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warn($"minimap map change failed: {ex.Message}");
            }
        });

    private void OnFixApplied(object? sender, Screenshots.PlayerFix fix) =>
        Dispatcher.UIThread.Post(Recenter);

    /// <summary>
    /// Puts the player in the middle at the configured range.
    /// </summary>
    /// <remarks>
    /// Always centered and always following, with no toggle. A minimap that can be panned away from
    /// the player is just a small map, and the whole point of this window is that a glance answers
    /// "what is around me" without any interaction at all.
    /// </remarks>
    private void Recenter()
    {
        if (_canvas.Map is not { } map)
            return;

        // Range is in game meters, so the amount of ground shown stays the same whatever the
        // window size or the map's own scale.
        var pixelsPerMeter = map.Projection.AverageScale;
        var halfExtent = Math.Max(1.0, _settings.MinimapRangeMeters * pixelsPerMeter);

        RangeText.Text = $"{_settings.MinimapRangeMeters:F0} m";

        var center = _session.Player.Current is { } fix
            ? map.ToBase(fix.Position)
            : map.SvgBaseRect.Center;

        var viewport = _canvas.Viewport;
        var shortest = Math.Min(Math.Max(viewport.Width, 1), Math.Max(viewport.Height, 1));

        _canvas.ShowAt(center, shortest / (halfExtent * 2.0));
    }

    private void Rescale(double factor)
    {
        _settings.MinimapRangeMeters = Math.Clamp(_settings.MinimapRangeMeters * factor, 25.0, 1000.0);
        _persist();
        Recenter();
    }

    /// <summary>Re-reads the range from settings, for when it was changed somewhere else.</summary>
    public void Rescale() => Recenter();

    // ---- Placement ----------------------------------------------------------

    private void RestorePlacement()
    {
        if (_settings.MinimapPlacement is not { } placement)
            return;

        Width = Math.Max(MinWidth, placement.Width);
        Height = Math.Max(MinHeight, placement.Height);

        // Only when it lands on a screen that still exists. Restoring onto a monitor that has been
        // unplugged puts the window somewhere the user cannot reach it, and this one has no taskbar
        // entry to recover it from.
        var position = new PixelPoint((int)placement.X, (int)placement.Y);

        if (Screens.All.Any(s => s.Bounds.Contains(position)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = position;
        }
    }

    private void SavePlacement() =>
        _settings.MinimapPlacement = new WindowPlacement
        {
            X = Position.X,
            Y = Position.Y,
            Width = Width,
            Height = Height,
        };

    private void Teardown()
    {
        _session.FixApplied -= OnFixApplied;
        _session.MapChanged -= OnMapChanged;
        _session.PoisChanged -= OnRedrawWanted;
        _session.WaypointsChanged -= OnRedrawWanted;
        _session.ExitAvailabilityChanged -= OnRedrawWanted;
        _session.QuestsChanged -= OnRedrawWanted;
        _session.RaidStateChanged -= OnRaidStateChanged;
        _session.SelectionChanged -= OnRedrawWanted;

        _session.Party.Changed -= OnRedrawWanted;
        _session.Party.RoutesChanged -= OnRedrawWanted;
        _session.PingAdded -= OnRedrawWanted;

        // The overlays belong to the session and are still in use by the main window; only the
        // canvas's references to them go.
        _canvas.ClearOverlays();

        _imageSource?.Dispose();
        _imageSource = null;

        SavePlacement();
        _persist();

        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Repaints, for whoever owns the animation clock.</summary>
    public void Redraw() => _canvas.InvalidateVisual();

    // ---- Click-through ------------------------------------------------------

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExLayered = 0x80000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    /// <summary>
    /// Makes the window ignore the mouse entirely, so clicks land on the game behind it.
    /// </summary>
    /// <remarks>
    /// WS_EX_TRANSPARENT applies to the whole window, header included, which is why the setting
    /// lives in the main window's preferences rather than on a control here: once it is on, nothing
    /// in this window can be clicked to turn it off again.
    /// </remarks>
    public void ApplyClickThrough()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle() is not { } handle)
            return;

        try
        {
            var current = (long)GetWindowLongPtr(handle.Handle, GwlExStyle);

            var wanted = _settings.MinimapClickThrough
                ? current | WsExTransparent | WsExLayered
                : current & ~WsExTransparent;

            SetWindowLongPtr(handle.Handle, GwlExStyle, (IntPtr)wanted);
        }
        catch (Exception ex)
        {
            // Worth having, not worth failing over: without it the minimap simply keeps taking
            // clicks, which is how it behaved a moment ago anyway.
            Diagnostics.Log.Warn($"could not set minimap click-through: {ex.Message}");
        }
    }
}
