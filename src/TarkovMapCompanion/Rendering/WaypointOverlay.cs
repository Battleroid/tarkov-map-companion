using SkiaSharp;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.Rendering;

/// <summary>One place the player means to visit, in order.</summary>
public sealed class Waypoint
{
    /// <summary>Game coordinates, so arrival can be judged in meters rather than pixels.</summary>
    public required GamePosition Position { get; init; }

    /// <summary>The same point in the map's base pixel space, for drawing.</summary>
    public required MapPoint Base { get; init; }

    /// <summary>Position in the route, from 1. Renumbered whenever one is removed.</summary>
    public int Number { get; internal set; }

    /// <summary>Reached, and awaiting removal on the next update.</summary>
    public bool Visited { get; internal set; }
}

/// <summary>
/// An ordered route the player has drawn on the map, and the pins along it.
/// </summary>
/// <remarks>
/// <para>
/// For planning a run: mark the quest objectives and the stashes worth a detour, in the order you
/// mean to take them, and the guide line walks you through them before handing you back to your
/// chosen exit. The route is the player's own; nothing here knows what a quest is.
/// </para>
/// <para>
/// Written by the folder-watcher thread as fixes arrive and by the UI thread as pins are placed,
/// and read by the render thread. Same rule as the player trail: everything mutating is behind the
/// lock, and readers get a snapshot rather than the live list.
/// </para>
/// </remarks>
public sealed class WaypointOverlay : IAnimatedOverlay
{
    private static readonly SKTypeface Typeface =
        SKTypeface.FromFamilyName("Cascadia Mono")
        ?? SKTypeface.FromFamilyName("Consolas")
        ?? SKTypeface.Default;

    /// <summary>Wall clock for the marching arrows. Shared, monotonic, and thread-safe to read.</summary>
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    private const float PinRadius = 10f;

    /// <summary>Minimum screen-space gap between arrowheads.</summary>
    private const float ArrowSpacing = 26f;

    /// <summary>Ceiling on arrowheads per route, so extreme zoom cannot make this expensive.</summary>
    private const int MaxArrowheads = 200;

    /// <summary>How many arrow-spacings the pattern travels each second.</summary>
    private const double ArrowsPerSecond = 0.8;

    private readonly object _gate = new();
    private readonly List<Waypoint> _waypoints = [];

    /// <summary>Drawn above the points of interest but below the guide line and the player.</summary>
    public int ZOrder => 800;

    public bool IsVisible { get; set; } = true;

    public GameMap? Map { get; set; }

    /// <summary>How close counts as reaching a waypoint, in meters.</summary>
    public double ArrivalRadiusMeters { get; set; } = 50.0;

    public WaypointArrival Arrival { get; set; } = WaypointArrival.MarkThenRemove;

    /// <summary>Whether clicking the map adds a waypoint rather than selecting an exit.</summary>
    public bool IsPlacing { get; set; }

    /// <summary>
    /// Whether the arrowheads travel along the route. Off still draws them, just stationary.
    /// </summary>
    /// <remarks>
    /// Turning this off is also what lets the shared clock stop, so it is the knob for anyone who
    /// would rather the app did nothing at all while they are not looking at it.
    /// </remarks>
    public bool AnimateArrows { get; set; } = true;

    /// <summary>Frames are only wanted while there is a route long enough to march along.</summary>
    public bool Advance() => AnimateArrows && (Count >= 2 || _shared.Any(r => r.Points.Count >= 2));

    /// <summary>
    /// A teammate's route, as they last published it.
    /// </summary>
    /// <remarks>
    /// Held in game coordinates and projected every frame rather than at receipt. A route outlives
    /// a map change on either end, and base pixel space belongs to one map, so keeping the game
    /// coordinates means there is no stale projection to invalidate -- for the cost of a handful of
    /// multiplies on at most a few dozen points.
    /// </remarks>
    public sealed record SharedRoute(string Owner, string Map, SKColor Color, IReadOnlyList<GamePosition> Points);

    private IReadOnlyList<SharedRoute> _shared = [];

    /// <summary>
    /// Replaces every teammate route. Ours is never among them.
    /// </summary>
    /// <remarks>
    /// Deliberately draw-only. These never touch <see cref="Next"/>, the guide line, or focus
    /// framing: a teammate dropping a pin must not redirect where your app is pointing or move your
    /// camera mid-raid, which would make the feature a griefing tool rather than a convenience.
    /// </remarks>
    public void SetSharedRoutes(IReadOnlyList<SharedRoute> routes) => _shared = routes;

    public int Count
    {
        get { lock (_gate) return _waypoints.Count; }
    }

    public bool Any => Count > 0;

    /// <summary>A snapshot of the route, safe to enumerate off the thread that owns it.</summary>
    public IReadOnlyList<Waypoint> Waypoints
    {
        get { lock (_gate) return _waypoints.ToArray(); }
    }

    /// <summary>
    /// The waypoint being navigated to: the first one not yet reached. Null once the route is
    /// finished, which is what hands the guide line back to the selected exit.
    /// </summary>
    public Waypoint? Next
    {
        get { lock (_gate) return _waypoints.FirstOrDefault(w => !w.Visited); }
    }

    /// <summary>
    /// Ceiling on a route, matching what one shared-route frame can carry.
    /// </summary>
    /// <remarks>
    /// Far past any real route. It exists because an unbounded one eventually produces a frame the
    /// receiving end rejects as corrupt, and the host answers corruption by dropping the peer --
    /// so without a cap, drawing enough markers quietly disconnects your squad.
    /// </remarks>
    public const int MaxWaypoints = 64;

    /// <summary>Adds a waypoint. Returns false when the route is already at its ceiling.</summary>
    public bool Add(GamePosition position, MapPoint basePoint)
    {
        lock (_gate)
        {
            if (_waypoints.Count >= MaxWaypoints)
                return false;

            _waypoints.Add(new Waypoint { Position = position, Base = basePoint });
            Renumber();
            return true;
        }
    }

    /// <summary>Removes the most recently placed waypoint. Returns false when there were none.</summary>
    public bool RemoveLast()
    {
        lock (_gate)
        {
            if (_waypoints.Count == 0)
                return false;

            _waypoints.RemoveAt(_waypoints.Count - 1);
            Renumber();
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
            _waypoints.Clear();
    }

    /// <summary>
    /// Folds a new player position into the route, retiring waypoints that have been reached.
    /// </summary>
    /// <returns>True when the route changed and the view needs redrawing.</returns>
    public bool ApplyFix(GamePosition player)
    {
        lock (_gate)
        {
            if (_waypoints.Count == 0)
                return false;

            // Retire whatever was shown as reached on the *previous* fix, before looking at this
            // one. That ordering is what gives a newly reached pin exactly one update on screen:
            // marked here, removed on the next call.
            var changed = _waypoints.RemoveAll(w => w.Visited) > 0;

            foreach (var waypoint in _waypoints)
            {
                // Any pin in range counts, not just the next one. Walking past number three on the
                // way to number one still means you were there.
                if (player.GroundDistanceTo(waypoint.Position) > ArrivalRadiusMeters)
                    continue;

                waypoint.Visited = true;
                changed = true;
            }

            if (Arrival == WaypointArrival.RemoveOnArrival)
                changed |= _waypoints.RemoveAll(w => w.Visited) > 0;

            if (changed)
                Renumber();

            return changed;
        }
    }

    private void Renumber()
    {
        for (var i = 0; i < _waypoints.Count; i++)
            _waypoints[i].Number = i + 1;
    }

    public void Draw(SKCanvas canvas, Viewport viewport)
    {
        // Teammates first, so your own route always draws over theirs.
        DrawSharedRoutes(canvas, viewport);

        var waypoints = Waypoints;
        if (waypoints.Count == 0)
            return;

        DrawRoute(canvas, viewport, waypoints);

        // The ring shows where the next pin will count as reached, so "am I close enough" is a
        // question you can answer by looking rather than by walking further in.
        if (waypoints.FirstOrDefault(w => !w.Visited) is { } next)
            DrawArrivalRing(canvas, viewport, next);

        foreach (var waypoint in waypoints)
            DrawPin(canvas, viewport, waypoint);
    }

    /// <summary>
    /// The line through the remaining waypoints, in order, with arrowheads marching along it.
    /// </summary>
    /// <remarks>
    /// A dashed line says "these points are connected". Arrowheads say which way around, which is
    /// the entire content of a route -- and a route drawn without it is a shape you have to read the
    /// pin numbers to interpret. The path is built in visiting order, so the tangent at any point
    /// along it already faces the next pin and the direction falls out of the geometry.
    /// </remarks>
    private void DrawRoute(SKCanvas canvas, Viewport viewport, IReadOnlyList<Waypoint> waypoints)
    {
        if (waypoints.Count < 2)
            return;

        using var path = new SKPath();

        for (var i = 0; i < waypoints.Count; i++)
        {
            var screen = viewport.ToScreen(waypoints[i].Base);

            if (i == 0)
                path.MoveTo((float)screen.X, (float)screen.Y);
            else
                path.LineTo((float)screen.X, (float)screen.Y);
        }

        // A faint continuous thread under the arrowheads, so the route still reads as one line
        // between them rather than as a row of unconnected marks.
        using var thread = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.4f,
            Color = MarkerPalette.Waypoint.WithAlpha(0x50),
        };

        canvas.DrawPath(path, thread);

        DrawArrowheads(canvas, path, MarkerPalette.Waypoint.WithAlpha(0xC0));
    }

    /// <summary>Teammates' routes, in their own colors, quieter than your own.</summary>
    private void DrawSharedRoutes(SKCanvas canvas, Viewport viewport)
    {
        if (Map is not { } map)
            return;

        foreach (var route in _shared)
        {
            // Map-gated like peers and pings, and for the same reason: coordinates from another
            // map mean something else entirely in this one.
            if (route.Points.Count == 0
                || !string.Equals(route.Map, map.NormalizedName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var path = new SKPath();

            for (var i = 0; i < route.Points.Count; i++)
            {
                var screen = viewport.ToScreen(map.ToBase(route.Points[i]));

                if (i == 0)
                    path.MoveTo((float)screen.X, (float)screen.Y);
                else
                    path.LineTo((float)screen.X, (float)screen.Y);
            }

            if (route.Points.Count > 1)
            {
                using var thread = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1.2f,
                    Color = route.Color.WithAlpha(0x40),
                };

                canvas.DrawPath(path, thread);
                DrawArrowheads(canvas, path, route.Color.WithAlpha(0x80));
            }

            DrawSharedPins(canvas, viewport, map, route);
        }
    }

    private static void DrawSharedPins(SKCanvas canvas, Viewport viewport, GameMap map, SharedRoute route)
    {
        using var fill = new SKPaint { IsAntialias = true, Color = route.Color.WithAlpha(0x9A) };
        using var edge = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.4f,
            Color = MarkerPalette.Halo.WithAlpha(0x9A),
        };

        for (var i = 0; i < route.Points.Count; i++)
        {
            var screen = viewport.ToScreen(map.ToBase(route.Points[i]));
            var x = (float)screen.X;
            var y = (float)screen.Y;

            // Smaller than your own pins, so a squad's worth of routes cannot bury the one you
            // are actually following.
            canvas.DrawCircle(x, y, PinRadius * 0.62f, fill);
            canvas.DrawCircle(x, y, PinRadius * 0.62f, edge);

            // The owner's name on the first pin only. On every pin it would be a wall of text the
            // moment two people share a four-point route.
            if (i == 0)
                DrawOwnerLabel(canvas, x + (PinRadius * 0.62f) + 4, y + 4, route.Owner);
        }
    }

    private static void DrawOwnerLabel(SKCanvas canvas, float x, float y, string text)
    {
        using var halo = new SKPaint
        {
            IsAntialias = true,
            Typeface = Typeface,
            TextSize = 11,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3,
            Color = MarkerPalette.Halo,
        };

        using var body = new SKPaint
        {
            IsAntialias = true,
            Typeface = Typeface,
            TextSize = 11,
            Color = MarkerPalette.LabelText.WithAlpha(0xCC),
        };

        canvas.DrawText(text, x, y, halo);
        canvas.DrawText(text, x, y, body);
    }

    /// <summary>Chevrons spaced along a path, pointing the way it runs.</summary>
    private void DrawArrowheads(SKCanvas canvas, SKPath path, SKColor color)
    {
        using var measure = new SKPathMeasure(path, false);
        var length = measure.Length;

        if (length < ArrowSpacing)
            return;

        // Zoomed in, a two-pin route can be tens of thousands of screen pixels long. Cap the count
        // and let the spacing stretch: a thousand arrowheads is slow to draw and no more legible
        // than twenty.
        var spacing = Math.Max(ArrowSpacing, length / MaxArrowheads);

        // Phase from wall time, never from a frame counter, so a dropped frame is a stutter rather
        // than an animation that has quietly fallen behind.
        var phase = AnimateArrows
            ? (float)(Clock.Elapsed.TotalSeconds * ArrowsPerSecond % 1.0) * spacing
            : spacing * 0.5f;

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Color = color,
        };

        for (var distance = phase; distance < length; distance += spacing)
        {
            if (!measure.GetPositionAndTangent(distance, out var at, out var tangent))
                continue;

            var angle = Math.Atan2(tangent.Y, tangent.X);

            canvas.Save();
            canvas.Translate(at.X, at.Y);
            canvas.RotateRadians((float)angle);

            // Drawn pointing along +X, so the rotation above aims it down the path.
            using var chevron = new SKPath();
            chevron.MoveTo(-3.5f, -3.5f);
            chevron.LineTo(3.0f, 0f);
            chevron.LineTo(-3.5f, 3.5f);

            canvas.DrawPath(chevron, paint);
            canvas.Restore();
        }
    }

    private void DrawArrivalRing(SKCanvas canvas, Viewport viewport, Waypoint next)
    {
        if (Map is not { } map)
            return;

        var radius = (float)(ArrivalRadiusMeters * map.Projection.AverageScale * viewport.Scale);

        // Below a few pixels the ring is just a smudge on the pin.
        if (radius < 6f)
            return;

        var screen = viewport.ToScreen(next.Base);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.2f,
            Color = MarkerPalette.Waypoint.WithAlpha(0x55),
        };

        canvas.DrawCircle((float)screen.X, (float)screen.Y, radius, paint);
    }

    private static void DrawPin(SKCanvas canvas, Viewport viewport, Waypoint waypoint)
    {
        var screen = viewport.ToScreen(waypoint.Base);
        var x = (float)screen.X;
        var y = (float)screen.Y;

        var color = waypoint.Visited ? MarkerPalette.WaypointVisited : MarkerPalette.Waypoint;

        using var fill = new SKPaint { IsAntialias = true, Color = color };
        using var edge = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.8f,
            Color = MarkerPalette.Halo,
        };

        canvas.DrawCircle(x, y, PinRadius, fill);
        canvas.DrawCircle(x, y, PinRadius, edge);

        // A reached pin gets a tick instead of its number: the number has stopped meaning
        // anything, and the tick says the arrival was registered rather than missed.
        if (waypoint.Visited)
        {
            using var tick = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.2f,
                Color = MarkerPalette.WaypointLabel,
                StrokeCap = SKStrokeCap.Round,
            };

            using var path = new SKPath();
            path.MoveTo(x - 4.5f, y);
            path.LineTo(x - 1.2f, y + 3.6f);
            path.LineTo(x + 5f, y - 3.6f);

            canvas.DrawPath(path, tick);
            return;
        }

        using var label = new SKPaint
        {
            IsAntialias = true,
            Typeface = Typeface,
            TextSize = 12,
            Color = MarkerPalette.WaypointLabel,
            TextAlign = SKTextAlign.Center,
            FakeBoldText = true,
        };

        canvas.DrawText(waypoint.Number.ToString(), x, y + 4.3f, label);
    }
}
