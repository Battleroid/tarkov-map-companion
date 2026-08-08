using SkiaSharp;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Screenshots;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// Draws where the player is, which way they are facing, and how they got there.
/// </summary>
/// <remarks>
/// The heading comes from <see cref="MapProjection.ScreenAngleDegrees"/>, which folds in the map's
/// coordinate rotation. The arrow artwork points up at zero degrees, matching that contract.
/// </remarks>
public sealed class PlayerOverlay : IMapOverlay
{
    private readonly List<PlayerFix> _history = [];

    public int ZOrder => 1000;

    public bool IsVisible { get; set; } = true;

    public GameMap? Map { get; set; }

    /// <summary>Most recent fix, or null before the first screenshot of the session.</summary>
    public PlayerFix? Current { get; private set; }

    /// <summary>How many past fixes to draw behind the player. 0 hides the trail.</summary>
    public int TrailLength { get; set; } = 12;

    /// <summary>Marker size in screen pixels. Constant regardless of zoom, like a map pin.</summary>
    public float MarkerSize { get; set; } = 22f;

    /// <summary>
    /// Floors currently displayed. A fix whose height belongs to some other floor is drawn
    /// dimmed rather than hidden -- losing the player marker entirely because they walked into a
    /// basement would be worse than showing it faintly.
    /// </summary>
    public IReadOnlyCollection<string> ActiveFloors { get; set; } = [];

    /// <summary>Fixes belonging to the raid in progress, oldest first.</summary>
    public IReadOnlyList<PlayerFix> History => _history;

    /// <summary>
    /// Longest a raid may run before a new fix is treated as a fresh one. Configurable because
    /// the real ceiling is per-map, from 20 minutes on Factory to 50 on Streets.
    /// </summary>
    public TimeSpan MaxRaidLength { get; set; } = RaidSession.DefaultMaxRaidLength;

    /// <summary>Real time elapsed since the first fix of the current raid.</summary>
    public TimeSpan RaidElapsed =>
        Current is null ? TimeSpan.Zero : RaidSession.ElapsedIn(_history, Current);

    /// <summary>Raised when a fix starts a new raid, after the old trail has been dropped.</summary>
    public event EventHandler? RaidStarted;

    public void Add(PlayerFix fix)
    {
        // A fix from a different raid must not be joined to the current trail: the line would run
        // straight across the map between two unrelated positions.
        if (_history.Count > 0 && !RaidSession.IsSameRaid(_history[^1], fix, MaxRaidLength))
        {
            _history.Clear();
            RaidStarted?.Invoke(this, EventArgs.Empty);
        }

        Current = fix;
        _history.Add(fix);

        ExpireOlderThanARaid(fix);

        // A raid cannot produce an unbounded number of screenshots, but cap anyway so a stuck
        // watcher cannot grow this without limit.
        const int cap = 512;
        if (_history.Count > cap)
            _history.RemoveRange(0, _history.Count - cap);
    }

    /// <summary>
    /// Drops trail points from further back than a raid can possibly run.
    /// </summary>
    /// <remarks>
    /// The clock heuristic in <see cref="RaidSession"/> catches obvious raid boundaries, but it
    /// cannot catch every one: two raids an hour apart whose in-raid clocks happen to line up look
    /// exactly like one long raid. Expiring old points is the backstop, and unlike splitting it
    /// cannot misfire -- the worst case is a slightly shorter trail.
    /// Measured on the in-raid clock, which is finer than the minute-resolution filename timestamp.
    /// </remarks>
    private void ExpireOlderThanARaid(PlayerFix newest)
    {
        var window = MaxRaidLength.TotalHours * RaidSession.GameClockRate;
        var cutoff = newest.RaidTimeHours - window;

        _history.RemoveAll(f => f.RaidTimeHours < cutoff);
    }

    public void Clear()
    {
        _history.Clear();
        Current = null;
    }

    public void Draw(SKCanvas canvas, Viewport viewport)
    {
        var map = Map;
        if (map is null || Current is null)
            return;

        DrawTrail(canvas, viewport, map);
        DrawPlayer(canvas, viewport, map, Current);
    }

    private void DrawTrail(SKCanvas canvas, Viewport viewport, GameMap map)
    {
        if (TrailLength <= 0 || _history.Count < 2)
            return;

        var start = Math.Max(0, _history.Count - TrailLength - 1);
        var points = _history.Skip(start).ToArray();
        if (points.Length < 2)
            return;

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true,
        };

        // Segment-by-segment so the trail can fade out towards the oldest point.
        for (var i = 1; i < points.Length; i++)
        {
            var from = viewport.ToScreen(map.ToBase(points[i - 1].Position));
            var to = viewport.ToScreen(map.ToBase(points[i].Position));

            var age = (double)i / points.Length;
            paint.Color = MarkerPalette.Trail.WithAlpha((byte)(30 + 150 * age));

            canvas.DrawLine((float)from.X, (float)from.Y, (float)to.X, (float)to.Y, paint);
        }

        // A small dot at each past fix makes stops distinguishable from a straight run.
        using var dot = new SKPaint
        {
            Color = MarkerPalette.Trail.WithAlpha(0x90),
            IsAntialias = true,
        };

        for (var i = 0; i < points.Length - 1; i++)
        {
            var screen = viewport.ToScreen(map.ToBase(points[i].Position));
            canvas.DrawCircle((float)screen.X, (float)screen.Y, 2.5f, dot);
        }
    }

    private void DrawPlayer(SKCanvas canvas, Viewport viewport, GameMap map, PlayerFix fix)
    {
        var screen = viewport.ToScreen(map.ToBase(fix.Position));
        var angle = map.Projection.ScreenAngleDegrees(fix.YawDegrees);

        var onFloor = IsOnDisplayedFloor(map, fix);
        var fillColor = onFloor ? MarkerPalette.Player : MarkerPalette.Dimmed(MarkerPalette.Player);

        using var fill = new SKPaint { Color = fillColor, IsAntialias = true };
        using var outline = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.75f,
            Color = MarkerPalette.PlayerOutline,
            IsAntialias = true,
        };

        var restore = canvas.Save();
        canvas.Translate((float)screen.X, (float)screen.Y);
        canvas.RotateDegrees((float)angle);

        var half = MarkerSize / 2f;

        // A chevron: unambiguous about which end is the front, unlike a circle with a tick.
        using var arrow = new SKPath();
        arrow.MoveTo(0, -half);
        arrow.LineTo(half * 0.62f, half * 0.72f);
        arrow.LineTo(0, half * 0.34f);
        arrow.LineTo(-half * 0.62f, half * 0.72f);
        arrow.Close();

        canvas.DrawPath(arrow, fill);
        canvas.DrawPath(arrow, outline);

        canvas.RestoreToCount(restore);
    }

    private bool IsOnDisplayedFloor(GameMap map, PlayerFix fix)
    {
        if (map.Floors.Count == 0)
            return true;

        // Any floor that claims this height and is switched on counts as a match.
        foreach (var floor in map.Floors)
        {
            if (floor.Covers(fix.Position))
                return ActiveFloors.Contains(floor.Name);
        }

        // No floor claims it, so it belongs to the base level, which is always drawn.
        return true;
    }
}
