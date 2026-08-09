using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// The map surface: draws base imagery and overlays through Skia, and handles pan and zoom.
/// </summary>
/// <remarks>
/// Everything is custom-drawn rather than composed from Avalonia controls. A map frame is one
/// image blit plus a few hundred markers; expressing that as a visual tree would cost far more
/// than it buys, and the heatmap needs raw canvas access regardless.
/// </remarks>
public sealed class MapCanvas : Control
{
    private const double WheelZoomStep = 1.15;

    /// <summary>
    /// How long a smoothed camera move takes.
    /// </summary>
    /// <remarks>
    /// Short enough that the map has settled well before the next screenshot, long enough to read
    /// as movement. The point of easing here is not decoration: when the view jumps, you have to
    /// re-find yourself on the map every time, whereas a move you can follow keeps your sense of
    /// which way is which.
    /// </remarks>
    private static readonly TimeSpan MoveDuration = TimeSpan.FromMilliseconds(320);

    private readonly List<IMapOverlay> _overlays = [];

    private IMapImageSource? _imageSource;
    private GameMap? _map;
    private Point? _dragOrigin;
    private bool _dragMoved;

    private DispatcherTimer? _moveTimer;
    private Stopwatch? _moveClock;
    private Viewport.State _moveFrom;
    private Viewport.State _moveTo;

    /// <summary>
    /// Until the user pans or zooms, the view keeps re-fitting to the whole map on every resize.
    /// A one-shot "fit after first layout" is not enough: the control can report a stale or
    /// pre-layout size when the map is set, which leaves the map opening part-way zoomed in.
    /// Re-fitting on resize is also what you want anyway while the view is still untouched.
    /// </summary>
    private bool _userHasAdjustedView;

    public MapCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>
    /// Fill behind the map, i.e. the letterbox around a map that does not fill the viewport.
    /// Styleable so it tracks the light/dark theme; the map artwork itself is dark either way.
    /// </summary>
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<MapCanvas>();

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public Viewport Viewport { get; private set; } = new(new MapRect(0, 0, 1, 1));

    public GameMap? Map => _map;

    /// <summary>Floors the user has switched on, by name.</summary>
    public HashSet<string> ActiveFloors { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the ground level is drawn. Off is what lets you actually see an underground floor:
    /// the map artwork stacks floors as opaque geometry, so Factory's Tunnels sit invisibly
    /// beneath the ground floor until it is hidden.
    /// </summary>
    public bool ShowBaseLayer { get; set; } = true;

    /// <summary>
    /// Ease camera moves instead of jumping. Only affects moves the app makes on the user's
    /// behalf -- following the player, framing an exit -- never their own panning and zooming,
    /// which must stay attached to the pointer.
    /// </summary>
    public bool SmoothMovement { get; set; }

    /// <summary>Raised on a click that was not the end of a drag.</summary>
    /// <remarks>
    /// Carries the modifier keys, because a plain click and a modified one mean different things
    /// on the map: selecting an exit versus pinging a spot.
    /// </remarks>
    public event EventHandler<MapClick>? Clicked;

    /// <summary>Raised as the pointer moves, with the base-space position, or null when it leaves.</summary>
    public event EventHandler<MapPoint?>? PointerMovedOverMap;

    public void SetMap(GameMap map, IMapImageSource imageSource)
    {
        // A move still running belongs to the map we are leaving, and its destination is a point in
        // a coordinate space that is about to stop existing.
        StopMoving();

        if (_imageSource is not null)
            _imageSource.Invalidated -= OnImageSourceInvalidated;

        _map = map;
        _imageSource = imageSource;
        _imageSource.Invalidated += OnImageSourceInvalidated;

        ActiveFloors.Clear();
        foreach (var floor in map.Floors.Where(f => f.ShownByDefault))
            ActiveFloors.Add(floor.Name);

        Viewport = new Viewport(map.BaseRect);
        Viewport.Resize(Bounds.Width, Bounds.Height);
        Viewport.FitAll();

        _userHasAdjustedView = false;

        InvalidateVisual();
    }

    /// <summary>Frames the whole map and re-arms automatic re-fitting on resize.</summary>
    public void FitAll()
    {
        StopMoving();
        Viewport.FitAll();
        _userHasAdjustedView = false;
        InvalidateVisual();
    }

    /// <summary>
    /// Frames two points with a margin, which is what extract-focus mode does on every new fix:
    /// as the player closes on the exit the view tightens, so the screen shows only what is
    /// relevant to getting there.
    /// </summary>
    public void FrameBoth(MapPoint a, MapPoint b, double padding)
    {
        var rect = MapRect.FromCorners(a, b);

        // Two points that are nearly coincident give a degenerate rect that would slam into the
        // maximum zoom. Keep a floor of roughly a 60 meter view.
        var minimum = Math.Max(Map?.Projection.AverageScale ?? 1, 1e-6) * 60;
        if (rect.Width < minimum || rect.Height < minimum)
        {
            var center = rect.Center;
            var half = minimum / 2;
            rect = new MapRect(center.X - half, center.Y - half, center.X + half, center.Y + half);
        }

        MoveTo(Viewport.StateForRect(rect, padding));
    }

    /// <summary>Centers on a point at the current zoom, which is what following the player does.</summary>
    public void CenterOn(MapPoint point) => MoveTo(Viewport.StateForCenter(point));

    /// <summary>Restores a previously captured view, e.g. on leaving extract-focus mode.</summary>
    public void RestoreView(Viewport.State state) => MoveTo(state);

    /// <summary>
    /// Moves the view to <paramref name="target"/>, easing there when smooth movement is on.
    /// </summary>
    private void MoveTo(Viewport.State target)
    {
        // Whoever is driving the view now owns it, so a resize must not refit to the whole map.
        _userHasAdjustedView = true;

        if (!SmoothMovement || Bounds is { Width: <= 0 } or { Height: <= 0 })
        {
            StopMoving();
            Viewport.Restore(target);
            InvalidateVisual();
            return;
        }

        // Ease from wherever the view is right now, not from where a previous move was aiming.
        // Retargeting mid-flight is the normal case: screenshots arrive faster than the animation
        // finishes when the player is moving quickly.
        _moveFrom = Viewport.Capture();
        _moveTo = target;

        if (Close(_moveFrom, _moveTo))
        {
            StopMoving();
            return;
        }

        _moveClock = Stopwatch.StartNew();

        _moveTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnMoveTick);

        _moveTimer.Start();
    }

    private void OnMoveTick(object? sender, EventArgs e)
    {
        if (_moveClock is null)
        {
            StopMoving();
            return;
        }

        var progress = Math.Clamp(_moveClock.Elapsed / MoveDuration, 0.0, 1.0);

        // Ease out: quick to start so it feels responsive, settling rather than stopping dead.
        var eased = 1.0 - Math.Pow(1.0 - progress, 3.0);

        // Zoom is interpolated geometrically. Doing it linearly makes the first half of a large
        // zoom change crawl and the second half lurch, because equal steps in scale are not equal
        // steps in apparent magnification.
        var scale = _moveFrom.Scale * Math.Pow(_moveTo.Scale / _moveFrom.Scale, eased);

        Viewport.Restore(new Viewport.State(
            new MapPoint(
                _moveFrom.Center.X + ((_moveTo.Center.X - _moveFrom.Center.X) * eased),
                _moveFrom.Center.Y + ((_moveTo.Center.Y - _moveFrom.Center.Y) * eased)),
            scale));

        if (progress >= 1.0)
            StopMoving();

        InvalidateVisual();
    }

    /// <summary>Ends any camera move in progress, leaving the view wherever it had reached.</summary>
    private void StopMoving()
    {
        _moveTimer?.Stop();
        _moveClock = null;
    }

    /// <summary>Whether two views are close enough that moving between them would not be visible.</summary>
    private bool Close(Viewport.State a, Viewport.State b)
    {
        // A base pixel is smaller than a screen pixel when zoomed out, so the threshold has to be
        // expressed in screen terms or a "tiny" move at low zoom is actually a large one.
        var dx = (a.Center.X - b.Center.X) * a.Scale;
        var dy = (a.Center.Y - b.Center.Y) * a.Scale;

        return (dx * dx) + (dy * dy) < 0.25
               && Math.Abs(Math.Log(a.Scale / b.Scale)) < 1e-4;
    }

    public void AddOverlay(IMapOverlay overlay)
    {
        _overlays.Add(overlay);
        _overlays.Sort((a, b) => a.ZOrder.CompareTo(b.ZOrder));
    }

    public void ClearOverlays() => _overlays.Clear();

    private void OnImageSourceInvalidated(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Viewport.Resize(e.NewSize.Width, e.NewSize.Height);

        if (!_userHasAdjustedView && e.NewSize is { Width: > 0, Height: > 0 })
            Viewport.FitAll();

        InvalidateVisual();
    }

    // ---- Input --------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        _dragOrigin = point.Position;
        _dragMoved = false;
        e.Pointer.Capture(this);
        Focus();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var position = e.GetPosition(this);

        if (_dragOrigin is { } origin && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var dx = position.X - origin.X;
            var dy = position.Y - origin.Y;

            // A couple of pixels of travel while clicking should still count as a click.
            if (Math.Abs(dx) + Math.Abs(dy) > 3)
                _dragMoved = true;

            if (_dragMoved)
            {
                // Dragging beats any move the app had started; the map must stay under the pointer.
                StopMoving();
                Viewport.PanByScreenDelta(dx, dy);
                _dragOrigin = position;
                _userHasAdjustedView = true;
                InvalidateVisual();
            }
        }

        PointerMovedOverMap?.Invoke(this, Viewport.ToBase(position.X, position.Y));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_dragOrigin is not null && !_dragMoved)
        {
            var position = e.GetPosition(this);
            Clicked?.Invoke(this, new MapClick(Viewport.ToBase(position.X, position.Y), e.KeyModifiers));
        }

        _dragOrigin = null;
        _dragMoved = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        PointerMovedOverMap?.Invoke(this, null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (Math.Abs(e.Delta.Y) < double.Epsilon)
            return;

        var position = e.GetPosition(this);
        var factor = e.Delta.Y > 0 ? WheelZoomStep : 1.0 / WheelZoomStep;

        StopMoving();
        Viewport.ZoomAt(position.X, position.Y, factor);
        _userHasAdjustedView = true;
        InvalidateVisual();
        e.Handled = true;
    }

    // ---- Rendering ----------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        if (Background is { } background)
            context.FillRectangle(background, new Rect(Bounds.Size));

        if (_map is null || _imageSource is null)
            return;

        context.Custom(new MapDrawOperation(
            new Rect(Bounds.Size),
            _imageSource,
            Viewport,
            ActiveFloors.ToArray(),
            ShowBaseLayer,
            _overlays.ToArray()));
    }

    /// <summary>
    /// Bridges Avalonia's drawing context to a raw <see cref="SKCanvas"/>.
    /// </summary>
    /// <remarks>
    /// The snapshot of overlays and floors is taken on the UI thread in <see cref="Render"/>;
    /// this operation may run on the render thread and must not read mutable control state.
    /// </remarks>
    private sealed class MapDrawOperation(
        Rect bounds,
        IMapImageSource imageSource,
        Viewport viewport,
        IReadOnlyCollection<string> activeFloors,
        bool showBaseLayer,
        IReadOnlyList<IMapOverlay> overlays) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;

        public bool HitTest(Point p) => Bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null)
                return;

            using var api = lease.Lease();
            var canvas = api.SkCanvas;

            var restore = canvas.Save();
            try
            {
                canvas.ClipRect(new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));

                imageSource.Draw(canvas, viewport, activeFloors, showBaseLayer);

                foreach (var overlay in overlays)
                {
                    if (overlay.IsVisible)
                        overlay.Draw(canvas, viewport);
                }
            }
            catch (Exception ex)
            {
                // A throwing overlay must not take the window's whole render pass with it.
                Console.Error.WriteLine($"map render: {ex}");
            }
            finally
            {
                canvas.RestoreToCount(restore);
            }
        }
    }
}

/// <summary>A click on the map, with whatever was held down at the time.</summary>
public readonly record struct MapClick(MapPoint Position, KeyModifiers Modifiers)
{
    public bool IsShift => Modifiers.HasFlag(KeyModifiers.Shift);
}

/// <summary>Something drawn on top of the base imagery: heatmap, POIs, the player marker.</summary>
public interface IMapOverlay
{
    /// <summary>Lower draws first. Base imagery is effectively 0.</summary>
    int ZOrder { get; }

    bool IsVisible { get; }

    /// <summary>Draws into screen space; use <paramref name="viewport"/> to place base-space geometry.</summary>
    void Draw(SKCanvas canvas, Viewport viewport);
}
