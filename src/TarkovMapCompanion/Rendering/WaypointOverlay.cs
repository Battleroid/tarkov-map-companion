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
public sealed class WaypointOverlay : IMapOverlay
{
    private static readonly SKTypeface Typeface =
        SKTypeface.FromFamilyName("Cascadia Mono")
        ?? SKTypeface.FromFamilyName("Consolas")
        ?? SKTypeface.Default;

    private const float PinRadius = 10f;

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

    public void Add(GamePosition position, MapPoint basePoint)
    {
        lock (_gate)
        {
            _waypoints.Add(new Waypoint { Position = position, Base = basePoint });
            Renumber();
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

    /// <summary>The line through the remaining waypoints, in order.</summary>
    private static void DrawRoute(SKCanvas canvas, Viewport viewport, IReadOnlyList<Waypoint> waypoints)
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

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.8f,
            Color = MarkerPalette.Waypoint.WithAlpha(0x90),
            PathEffect = SKPathEffect.CreateDash([4f, 5f], 0),
        };

        canvas.DrawPath(path, paint);
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
