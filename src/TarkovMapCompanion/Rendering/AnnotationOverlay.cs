using SkiaSharp;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// Draws the text people have written on the map, theirs and yours.
/// </summary>
/// <remarks>
/// <para>
/// Sits above the point-of-interest layers and below the route, because a label is context for
/// what is already there rather than something you navigate by. A route built through a labeled
/// building should draw over its name, not under it.
/// </para>
/// <para>
/// Labels are the one thing here that gets denser as you zoom out, since the map shrinks and the
/// text does not. Below a readable scale they stop being drawn rather than turning the map into a
/// wall of overlapping words.
/// </para>
/// </remarks>
public sealed class AnnotationOverlay : IMapOverlay, ILabeledOverlay
{
    /// <summary>Above the POI layers, below quests and the route.</summary>
    public const int Layer = 550;

    /// <summary>
    /// Below this many screen pixels per game meter, labels are hidden.
    /// </summary>
    /// <remarks>
    /// Zoomed out to a whole map, two hundred building names occupy more pixels than the map does.
    /// Hiding them is the honest failure: the alternative is a solid block of text with the map
    /// somewhere underneath it.
    /// </remarks>
    private const double MinScaleForText = 0.35;

    private const float AnchorRadius = 2.5f;
    private const double HitSlack = 8.0;

    private static readonly SKTypeface LabelTypeface =
        SKTypeface.FromFamilyName("Cascadia Mono")
        ?? SKTypeface.FromFamilyName("Consolas")
        ?? SKTypeface.Default;

    private static readonly SKPaint LabelHalo = new()
    {
        IsAntialias = true,
        Typeface = LabelTypeface,
        TextSize = 12,
        TextAlign = SKTextAlign.Center,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 3,
        Color = MarkerPalette.Halo,
    };

    private static readonly SKPaint LabelBody = new()
    {
        IsAntialias = true,
        Typeface = LabelTypeface,
        TextSize = 12,
        TextAlign = SKTextAlign.Center,
    };

    private readonly object _gate = new();
    private IReadOnlyList<MapAnnotation> _annotations = [];

    public int ZOrder => Layer;

    public bool IsVisible { get; set; } = true;

    /// <inheritdoc />
    public LabelPlacer? Labels { get; set; }

    public GameMap? Map { get; set; }

    /// <summary>The one under the pointer, drawn brighter and with its author.</summary>
    public MapAnnotation? Hovered { get; set; }

    /// <summary>Color for your own notes.</summary>
    public SKColor Color { get; set; } = MarkerPalette.Annotation;

    /// <summary>Works out the color a teammate's notes are drawn in. Set by the session.</summary>
    public Func<string, SKColor>? SharedColor { get; set; }

    public IReadOnlyList<MapAnnotation> Annotations
    {
        get { lock (_gate) return _annotations; }
    }

    public void SetAnnotations(IReadOnlyList<MapAnnotation> annotations)
    {
        lock (_gate)
        {
            _annotations = annotations;
            Hovered = null;
        }
    }

    /// <summary>Nearest label within reach of a screen point, or null.</summary>
    public MapAnnotation? HitTest(Viewport viewport, double screenX, double screenY)
    {
        if (Map is not { } map)
            return null;

        MapAnnotation? best = null;
        var bestDistance = double.MaxValue;

        foreach (var annotation in Annotations)
        {
            if (!string.Equals(annotation.Map, map.NormalizedName, StringComparison.OrdinalIgnoreCase))
                continue;

            var screen = viewport.ToScreen(map.Projection.ToBase(annotation.X, annotation.Z));

            // Generous horizontally, because the target people aim at is the text rather than the
            // dot underneath it.
            var width = Math.Max(24.0, LabelBody.MeasureText(annotation.Text) / 2.0 + HitSlack);

            var dx = Math.Abs(screen.X - screenX);
            var dy = Math.Abs(screen.Y - screenY);

            if (dx > width || dy > 14)
                continue;

            var distance = dx * dx + dy * dy;
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = annotation;
        }

        return best;
    }

    public void Draw(SKCanvas canvas, Viewport viewport)
    {
        if (Map is not { } map)
            return;

        var annotations = Annotations;
        if (annotations.Count == 0)
            return;

        // Screen pixels per game meter, which is what decides whether text is worth drawing.
        var perMeter = viewport.Scale * map.Projection.AverageScale;
        var showText = perMeter >= MinScaleForText;

        var visible = viewport.VisibleBaseRect.Inflate(0.1);

        using var dot = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        foreach (var annotation in annotations)
        {
            if (!string.Equals(annotation.Map, map.NormalizedName, StringComparison.OrdinalIgnoreCase))
                continue;

            var basePoint = map.Projection.ToBase(annotation.X, annotation.Z);
            if (!visible.Contains(basePoint))
                continue;

            var screen = viewport.ToScreen(basePoint);
            var x = (float)screen.X;
            var y = (float)screen.Y;

            var color = annotation.Author is { } author && SharedColor is { } lookup
                ? lookup(author)
                : Color;

            var hovered = ReferenceEquals(annotation, Hovered);

            // The anchor is always drawn, even when the text is not. It is what tells you there is
            // something written here worth zooming in for.
            dot.Color = color;
            canvas.DrawCircle(x, y, hovered ? AnchorRadius + 1.5f : AnchorRadius, dot);

            if (!showText && !hovered)
                continue;

            var label = hovered && annotation.Author is { } who ? $"{annotation.Text} — {who}" : annotation.Text;

            // Above the anchor, so the dot marks the spot and the words do not cover it.
            const float Rise = 7f;

            LabelBody.Color = hovered ? MarkerPalette.LabelText : color;

            if (Labels is not { } placer)
            {
                canvas.DrawText(label, x, y - Rise, LabelHalo);
                canvas.DrawText(label, x, y - Rise, LabelBody);
                continue;
            }

            if (placer.PlaceAbove(x, y, Rise, label, LabelBody) is not { } spot)
                continue;

            using (var leader = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                Color = color.WithAlpha(140),
            })
            {
                spot.DrawLeader(canvas, x, y, LabelBody.MeasureText(label), leader);
            }

            canvas.DrawText(label, spot.X, spot.Y, LabelHalo);
            canvas.DrawText(label, spot.X, spot.Y, LabelBody);
        }
    }
}
