using SkiaSharp;
using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// Draws the objectives of the quests you are tracking, and answers what is under the cursor.
/// </summary>
/// <remarks>
/// <para>
/// Only tracked tasks are drawn, and that is the whole design rather than a limitation. Lighthouse
/// alone has 169 positioned objectives; drawing every quest at once would bury the map under marks
/// for tasks you finished forty levels ago.
/// </para>
/// <para>
/// Zones are areas and are drawn as areas. A "visit" objective is a room or a yard, and the
/// difference between a filled outline and a dot is the difference between "stand in this room" and
/// "somewhere near here". Where upstream gives only a point, a point is what gets drawn.
/// </para>
/// </remarks>
public sealed class QuestOverlay : IMapOverlay
{
    /// <summary>Above the POI layers, below the route. A route built from quests draws over them.</summary>
    public const int Layer = 600;

    private const float MarkerRadius = 7f;
    private const double HitSlack = 6.0;

    /// <summary>Fill for a zone footprint. Faint: it is context, not the marker.</summary>
    private const byte ZoneFillAlpha = 40;

    private const byte ZoneStrokeAlpha = 150;

    private static readonly SKTypeface LabelTypeface =
        SKTypeface.FromFamilyName("Cascadia Mono")
        ?? SKTypeface.FromFamilyName("Consolas")
        ?? SKTypeface.Default;

    private static readonly SKPaint LabelHalo = new()
    {
        IsAntialias = true,
        Typeface = LabelTypeface,
        TextSize = 12,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 3,
        Color = MarkerPalette.Halo,
    };

    private static readonly SKPaint LabelBody = new()
    {
        IsAntialias = true,
        Typeface = LabelTypeface,
        TextSize = 12,
        Color = MarkerPalette.LabelText,
    };

    /// <summary>Number inside a marker, when one task has several objectives on this map.</summary>
    private static readonly SKPaint IndexBody = new()
    {
        IsAntialias = true,
        Typeface = LabelTypeface,
        TextSize = 9,
        TextAlign = SKTextAlign.Center,
        Color = MarkerPalette.PlayerOutline,
    };

    private readonly object _gate = new();
    private IReadOnlyList<QuestMark> _marks = [];

    public int ZOrder => Layer;

    public bool IsVisible { get; set; } = true;

    public GameMap? Map { get; set; }

    /// <summary>Label every mark with its task name, rather than only the one under the pointer.</summary>
    public bool ShowNames { get; set; } = true;

    /// <summary>The mark under the pointer, drawn larger and always labeled.</summary>
    public QuestMark? Hovered { get; set; }

    public IReadOnlyList<QuestMark> Marks
    {
        get { lock (_gate) return _marks; }
    }

    /// <summary>Replaces everything drawn. Called when the tracked set or the map changes.</summary>
    public void SetMarks(IReadOnlyList<QuestMark> marks)
    {
        lock (_gate)
        {
            _marks = marks;
            Hovered = null;
        }
    }

    public void Clear() => SetMarks([]);

    /// <summary>Nearest mark within the hit radius of a screen point, or null.</summary>
    public QuestMark? HitTest(Viewport viewport, double screenX, double screenY)
    {
        if (Map is not { } map)
            return null;

        QuestMark? best = null;
        var bestDistance = double.MaxValue;

        foreach (var mark in Marks)
        {
            var screen = viewport.ToScreen(map.ToBase(mark.Position));

            var dx = screen.X - screenX;
            var dy = screen.Y - screenY;
            var distance = dx * dx + dy * dy;

            var radius = MarkerRadius + HitSlack;

            if (distance <= radius * radius && distance < bestDistance)
            {
                bestDistance = distance;
                best = mark;
            }
        }

        return best;
    }

    public void Draw(SKCanvas canvas, Viewport viewport)
    {
        if (Map is not { } map)
            return;

        var marks = Marks;
        if (marks.Count == 0)
            return;

        var visible = viewport.VisibleBaseRect.Inflate(0.1);

        using var fill = new SKPaint { IsAntialias = true };
        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = new SKColor(0, 0, 0, 190),
        };

        // Footprints first, all of them, so a marker is never hidden under a later zone's fill.
        foreach (var mark in marks)
            DrawZone(canvas, viewport, map, mark, fill, stroke);

        foreach (var mark in marks)
            DrawMark(canvas, viewport, map, visible, mark, fill, stroke);
    }

    private static void DrawZone(
        SKCanvas canvas, Viewport viewport, GameMap map, QuestMark mark, SKPaint fill, SKPaint stroke)
    {
        if (mark.Outline.Count < 3)
            return;

        using var path = new SKPath();

        for (var i = 0; i < mark.Outline.Count; i++)
        {
            var (x, z) = mark.Outline[i];
            var screen = viewport.ToScreen(map.Projection.ToBase(x, z));

            if (i == 0)
                path.MoveTo((float)screen.X, (float)screen.Y);
            else
                path.LineTo((float)screen.X, (float)screen.Y);
        }

        path.Close();

        fill.Style = SKPaintStyle.Fill;
        fill.Color = mark.Color.WithAlpha(ZoneFillAlpha);
        canvas.DrawPath(path, fill);

        var previous = stroke.Color;
        stroke.Color = mark.Color.WithAlpha(ZoneStrokeAlpha);
        canvas.DrawPath(path, stroke);
        stroke.Color = previous;
    }

    private void DrawMark(
        SKCanvas canvas,
        Viewport viewport,
        GameMap map,
        MapRect visible,
        QuestMark mark,
        SKPaint fill,
        SKPaint stroke)
    {
        var basePoint = map.ToBase(mark.Position);
        if (!visible.Contains(basePoint))
            return;

        var screen = viewport.ToScreen(basePoint);
        var x = (float)screen.X;
        var y = (float)screen.Y;

        var hovered = ReferenceEquals(mark, Hovered);
        var radius = hovered ? MarkerRadius + 2.5f : MarkerRadius;

        fill.Style = SKPaintStyle.Fill;
        fill.Color = mark.Color;

        if (mark.OneOf)
        {
            // A hollow ring for "it might be here". A filled marker at each of five spawn points
            // would have you walk confidently past four of them.
            fill.Style = SKPaintStyle.Stroke;
            fill.StrokeWidth = 2.5f;
            canvas.DrawCircle(x, y, radius, fill);
            fill.Style = SKPaintStyle.Fill;
            fill.StrokeWidth = 0;
        }
        else
        {
            DrawPentagon(canvas, x, y, radius, fill, stroke);
        }

        // The number only appears when there is more than one on this map for the same task, so a
        // single-objective quest gets a clean marker.
        if (mark.Index > 0 && !mark.OneOf)
            canvas.DrawText(mark.Index.ToString(), x, y + 3.2f, IndexBody);

        if (!hovered && !ShowNames)
            return;

        var label = hovered ? mark.Label : mark.TaskName;
        var offset = radius + 5f;

        canvas.DrawText(label, x + offset, y + 4, LabelHalo);
        canvas.DrawText(label, x + offset, y + 4, LabelBody);
    }

    /// <summary>
    /// A five-sided marker, so quests read differently from the round loot markers and the diamond
    /// exits even without color.
    /// </summary>
    private static void DrawPentagon(SKCanvas canvas, float x, float y, float radius, SKPaint fill, SKPaint stroke)
    {
        using var path = new SKPath();

        for (var i = 0; i < 5; i++)
        {
            var angle = -Math.PI / 2 + i * 2 * Math.PI / 5;
            var px = x + (float)(Math.Cos(angle) * radius);
            var py = y + (float)(Math.Sin(angle) * radius);

            if (i == 0)
                path.MoveTo(px, py);
            else
                path.LineTo(px, py);
        }

        path.Close();

        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, stroke);
    }
}

/// <summary>
/// One place on the current map where a tracked objective happens.
/// </summary>
/// <param name="Index">
/// Position within its task on this map, 1-based, or 0 when the task has only one.
/// </param>
/// <param name="OneOf">One of several places the thing might be, rather than where it is.</param>
/// <param name="Outline">The zone footprint in game <c>(x, z)</c>, empty for a bare point.</param>
/// <remarks>
/// Holds game coordinates and projects every frame, the same as shared party routes do. Base pixel
/// space belongs to one map, so keeping game coordinates means there is no stale projection to
/// invalidate when the map changes, for the cost of a few multiplies on at most a few dozen points.
/// </remarks>
public sealed record QuestMark(
    string TaskId,
    string TaskName,
    string ObjectiveId,
    string Description,
    SKColor Color,
    GamePosition Position,
    int Index,
    bool OneOf,
    IReadOnlyList<(double X, double Z)> Outline)
{
    /// <summary>What to show when the pointer is on it: the task, then what it wants.</summary>
    public string Label =>
        string.IsNullOrWhiteSpace(Description) ? TaskName : $"{TaskName}: {Description}";
}
