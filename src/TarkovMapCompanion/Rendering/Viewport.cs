using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// The window onto a map: where we are looking in base space, and how magnified it is.
/// </summary>
/// <remarks>
/// Zoom here is continuous rather than the integer levels Leaflet uses, so the extract-focus mode
/// can frame a rect exactly instead of snapping to the nearest power of two. Tile lookups convert
/// back to an integer level when they need one.
/// </remarks>
public sealed class Viewport
{
    private MapPoint _center;
    private double _scale = 1.0;

    public Viewport(MapRect mapBounds)
    {
        MapBounds = mapBounds;
        _center = mapBounds.Center;
    }

    /// <summary>Extent of the map in base space. Panning is constrained relative to this.</summary>
    public MapRect MapBounds { get; private set; }

    /// <summary>Size of the drawing surface in device-independent pixels.</summary>
    public double Width { get; private set; }

    /// <inheritdoc cref="Width"/>
    public double Height { get; private set; }

    /// <summary>Screen pixels per base pixel.</summary>
    public double Scale
    {
        get => _scale;
        set
        {
            _scale = Math.Clamp(value, MinScale, MaxScale);
            ClampCenter();
        }
    }

    public MapPoint Center
    {
        get => _center;
        set
        {
            _center = value;
            ClampCenter();
        }
    }

    public double MinScale { get; set; } = 0.05;
    public double MaxScale { get; set; } = 200.0;

    /// <summary>Base-space rect currently visible.</summary>
    public MapRect VisibleBaseRect
    {
        get
        {
            var halfWidth = Width / 2.0 / _scale;
            var halfHeight = Height / 2.0 / _scale;
            return new MapRect(
                _center.X - halfWidth,
                _center.Y - halfHeight,
                _center.X + halfWidth,
                _center.Y + halfHeight);
        }
    }

    public void SetMapBounds(MapRect bounds)
    {
        MapBounds = bounds;
        ClampCenter();
    }

    public void Resize(double width, double height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        ClampCenter();
    }

    public MapPoint ToScreen(MapPoint basePoint) => new(
        (basePoint.X - _center.X) * _scale + Width / 2.0,
        (basePoint.Y - _center.Y) * _scale + Height / 2.0);

    public MapPoint ToBase(double screenX, double screenY) => new(
        (screenX - Width / 2.0) / _scale + _center.X,
        (screenY - Height / 2.0) / _scale + _center.Y);

    /// <summary>Screen rect for a base-space rect.</summary>
    public MapRect ToScreen(MapRect baseRect)
    {
        var topLeft = ToScreen(new MapPoint(baseRect.Left, baseRect.Top));
        var bottomRight = ToScreen(new MapPoint(baseRect.Right, baseRect.Bottom));
        return new MapRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
    }

    public void PanByScreenDelta(double dx, double dy) =>
        Center = new MapPoint(_center.X - dx / _scale, _center.Y - dy / _scale);

    /// <summary>
    /// Zooms about a fixed screen point, so the base-space position under the cursor stays put.
    /// </summary>
    public void ZoomAt(double screenX, double screenY, double factor)
    {
        var anchor = ToBase(screenX, screenY);
        var previous = _scale;

        Scale = _scale * factor;

        // Nothing to correct if the clamp swallowed the change.
        if (Math.Abs(_scale - previous) < double.Epsilon)
            return;

        var afterAnchor = ToBase(screenX, screenY);
        Center = new MapPoint(
            _center.X + (anchor.X - afterAnchor.X),
            _center.Y + (anchor.Y - afterAnchor.Y));
    }

    /// <summary>
    /// Frames <paramref name="target"/>, growing it by <paramref name="padding"/> of its own size
    /// first. This is what extract-focus mode drives every time a new fix arrives.
    /// </summary>
    public void FitToRect(MapRect target, double padding = 0.0)
    {
        if (Width <= 0 || Height <= 0)
            return;

        Restore(StateForRect(target, padding));
    }

    /// <summary>
    /// The view that <see cref="FitToRect"/> would produce, without moving there.
    /// </summary>
    /// <remarks>
    /// Separated out so the canvas can ease towards a view rather than snapping to it: an animation
    /// needs to know where it is going before it starts. Clamping is applied here too, so the
    /// animation's destination is the same place a jump would have landed.
    /// </remarks>
    public State StateForRect(MapRect target, double padding = 0.0)
    {
        if (Width <= 0 || Height <= 0)
            return Capture();

        var padded = target.Inflate(padding);

        // A player standing on top of the extract collapses the rect to a point; keep a floor so
        // the fit does not divide by zero and slam into MaxScale.
        var width = Math.Max(padded.Width, 1e-6);
        var height = Math.Max(padded.Height, 1e-6);

        var scale = Math.Clamp(Math.Min(Width / width, Height / height), MinScale, MaxScale);

        return new State(ClampedCenter(padded.Center, scale), scale);
    }

    /// <summary>The view centered on a point at the current zoom, without moving there.</summary>
    public State StateForCenter(MapPoint center) => new(ClampedCenter(center, _scale), _scale);

    /// <summary>Scale at which the whole map just fits the current viewport.</summary>
    public double FitAllScale()
    {
        if (Width <= 0 || Height <= 0 || MapBounds.Width <= 0 || MapBounds.Height <= 0)
            return 1.0;

        return Math.Min(Width / MapBounds.Width, Height / MapBounds.Height);
    }

    public void FitAll() => FitToRect(MapBounds);

    /// <summary>A snapshot of where the view is, so a temporary mode can hand it back afterwards.</summary>
    public readonly record struct State(MapPoint Center, double Scale);

    public State Capture() => new(_center, _scale);

    public void Restore(State state)
    {
        // Scale first: the centre clamp depends on how much of the map is on screen.
        Scale = state.Scale;
        Center = state.Center;
    }

    /// <summary>
    /// Keeps the map from being dragged off into empty space. When the map is smaller than the
    /// viewport on an axis it is centered on that axis; otherwise the center is held inside the
    /// map so at least half a screen of content is always visible.
    /// </summary>
    private void ClampCenter() => _center = ClampedCenter(_center, _scale);

    /// <inheritdoc cref="ClampCenter"/>
    private MapPoint ClampedCenter(MapPoint center, double scale)
    {
        if (Width <= 0 || Height <= 0)
            return center;

        var halfWidth = Width / 2.0 / scale;
        var halfHeight = Height / 2.0 / scale;

        var x = MapBounds.Width <= halfWidth * 2
            ? MapBounds.Center.X
            : Math.Clamp(center.X, MapBounds.Left, MapBounds.Right);

        var y = MapBounds.Height <= halfHeight * 2
            ? MapBounds.Center.Y
            : Math.Clamp(center.Y, MapBounds.Top, MapBounds.Bottom);

        return new MapPoint(x, y);
    }
}
