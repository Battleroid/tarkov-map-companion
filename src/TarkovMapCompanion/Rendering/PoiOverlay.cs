using SkiaSharp;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// Draws extracts, spawns, loot and hazards, and answers "what is under the cursor".
/// </summary>
/// <remarks>
/// Markers are a fixed screen size rather than scaling with zoom, so they stay usable when zoomed
/// right out. Hit-testing therefore also happens in screen space, against the last frame's
/// viewport -- which is exactly what the user was looking at when they moved the pointer.
/// </remarks>
public sealed class PoiOverlay : IMapOverlay
{
    private const float MarkerRadius = 5.5f;
    private const float ExtractRadius = 7.5f;

    /// <summary>Slack around a marker for hit-testing, in screen pixels.</summary>
    private const double HitSlack = 6.0;

    /// <summary>
    /// Text resources are shared and long-lived. Building an SKPaint (and worse, resolving a
    /// typeface by family name) per label per frame cost about 10 ms a frame on Customs, which is
    /// most of a 60 fps budget spent on font lookups.
    /// </summary>
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

    private IReadOnlyList<MapPoi> _pois = [];

    /// <summary>
    /// POIs split by layer, so a hidden layer costs nothing to skip. Customs alone has 551 loot
    /// containers that are off by default; walking past them twice a frame is pure waste.
    /// </summary>
    private readonly Dictionary<PoiKind, List<MapPoi>> _byKind = [];

    /// <summary>Extracts and transits, drawn last so they sit above the dense layers.</summary>
    private static readonly PoiKind[] ExtractKinds =
        [PoiKind.ExtractPmc, PoiKind.ExtractScav, PoiKind.ExtractShared, PoiKind.Transit];

    public int ZOrder => 500;

    public bool IsVisible { get; set; } = true;

    public GameMap? Map { get; set; }

    /// <summary>Which layers are drawn. Missing entries are treated as hidden.</summary>
    public IDictionary<PoiKind, bool> Visible { get; } = new Dictionary<PoiKind, bool>();

    /// <summary>Currently selected extract or transit, drawn with its outline and a label.</summary>
    public MapPoi? Selected { get; set; }

    /// <summary>POI under the pointer, drawn highlighted.</summary>
    public MapPoi? Hovered { get; set; }

    /// <summary>Labels every extract rather than only the selected one.</summary>
    public bool ShowExtractNames { get; set; } = true;

    public IReadOnlyList<MapPoi> Pois => _pois;

    public void SetPois(IReadOnlyList<MapPoi> pois)
    {
        _pois = pois;
        Selected = null;
        Hovered = null;

        _byKind.Clear();
        foreach (var poi in pois)
        {
            if (!_byKind.TryGetValue(poi.Kind, out var bucket))
                _byKind[poi.Kind] = bucket = [];

            bucket.Add(poi);
        }
    }

    public bool IsKindVisible(PoiKind kind) => Visible.TryGetValue(kind, out var on) && on;

    /// <summary>All extracts and transits, for the picker list.</summary>
    public IEnumerable<MapPoi> Extracts =>
        _pois.Where(p => p.IsExtract || p.Kind == PoiKind.Transit);

    /// <summary>
    /// Nearest visible POI within the hit radius of a screen point, or null.
    /// Extracts win ties: they are the ones people are actually reaching for.
    /// </summary>
    public MapPoi? HitTest(Viewport viewport, double screenX, double screenY)
    {
        MapPoi? best = null;
        var bestScore = double.MaxValue;

        foreach (var poi in _pois)
        {
            if (!IsKindVisible(poi.Kind))
                continue;

            var screen = viewport.ToScreen(poi.Base);
            var radius = (poi.IsExtract || poi.Kind == PoiKind.Transit ? ExtractRadius : MarkerRadius) + HitSlack;

            var dx = screen.X - screenX;
            var dy = screen.Y - screenY;
            var distanceSquared = dx * dx + dy * dy;

            if (distanceSquared > radius * radius)
                continue;

            // Bias towards extracts so a loot marker sitting on top of one does not steal the hit.
            var score = distanceSquared - (poi.IsExtract || poi.Kind == PoiKind.Transit ? 400 : 0);

            if (score < bestScore)
            {
                bestScore = score;
                best = poi;
            }
        }

        return best;
    }

    public void Draw(SKCanvas canvas, Viewport viewport)
    {
        if (_pois.Count == 0)
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

        // Background layers first, exits last, so an extract is never buried under a loot marker.
        foreach (var (kind, bucket) in _byKind)
        {
            if (ExtractKinds.Contains(kind) || !IsKindVisible(kind))
                continue;

            foreach (var poi in bucket)
                DrawOne(canvas, viewport, visible, poi, fill, stroke);
        }

        foreach (var kind in ExtractKinds)
        {
            if (!IsKindVisible(kind) || !_byKind.TryGetValue(kind, out var bucket))
                continue;

            foreach (var poi in bucket)
                DrawOne(canvas, viewport, visible, poi, fill, stroke);
        }

        if (Selected is { } selected)
        {
            // The chosen exit is drawn whatever the layer filters say. Filtering out the exit you
            // are currently navigating to, and leaving a guide line pointing at nothing, would be
            // worse than showing one marker the filter would otherwise hide.
            if (!IsKindVisible(selected.Kind))
                DrawOne(canvas, viewport, visible, selected, fill, stroke, force: true);

            DrawSelection(canvas, viewport, selected);
        }
    }

    private void DrawOne(
        SKCanvas canvas,
        Viewport viewport,
        MapRect visible,
        MapPoi poi,
        SKPaint fill,
        SKPaint stroke,
        bool force = false)
    {
        if ((!force && !IsKindVisible(poi.Kind)) || !visible.Contains(poi.Base))
            return;

        var screen = viewport.ToScreen(poi.Base);
        var x = (float)screen.X;
        var y = (float)screen.Y;

        var color = ColorFor(poi.Kind);
        var isExtract = poi.IsExtract || poi.Kind == PoiKind.Transit;
        var radius = isExtract ? ExtractRadius : MarkerRadius;

        if (ReferenceEquals(poi, Hovered) || ReferenceEquals(poi, Selected))
            radius += 2.5f;

        fill.Color = color;

        if (isExtract)
        {
            // Diamond for exits so they read differently from the round loot and spawn markers
            // even in grayscale.
            using var diamond = new SKPath();
            diamond.MoveTo(x, y - radius);
            diamond.LineTo(x + radius, y);
            diamond.LineTo(x, y + radius);
            diamond.LineTo(x - radius, y);
            diamond.Close();

            canvas.DrawPath(diamond, fill);
            canvas.DrawPath(diamond, stroke);
        }
        else
        {
            canvas.DrawCircle(x, y, radius, fill);
            canvas.DrawCircle(x, y, radius, stroke);
        }

        // A ring around a conditional exit, so "this one needs something" is visible at a glance
        // rather than only after clicking it. Deliberately a shape difference, not just a color.
        if (isExtract && poi.IsConditional)
        {
            using var ring = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.6f,
                Color = MarkerPalette.ConditionalRing,
                PathEffect = SKPathEffect.CreateDash([3f, 2.5f], 0),
            };

            canvas.DrawCircle(x, y, radius + 3.5f, ring);
        }

        var labeled = isExtract && (ShowExtractNames || ReferenceEquals(poi, Hovered) || ReferenceEquals(poi, Selected));
        if (labeled)
            DrawLabel(canvas, x + radius + 4, y + 4, poi.Name);
    }

    private void DrawSelection(SKCanvas canvas, Viewport viewport, MapPoi selected)
    {
        if (selected.Outline is not { Count: > 2 })
            return;

        using var path = new SKPath();
        for (var i = 0; i < selected.Outline.Count; i++)
        {
            var screen = viewport.ToScreen(selected.Outline[i]);
            if (i == 0)
                path.MoveTo((float)screen.X, (float)screen.Y);
            else
                path.LineTo((float)screen.X, (float)screen.Y);
        }
        path.Close();

        var color = ColorFor(selected.Kind);

        using var area = new SKPaint { IsAntialias = true, Color = color.WithAlpha(0x38) };
        using var edge = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            Color = color,
        };

        canvas.DrawPath(path, area);
        canvas.DrawPath(path, edge);
    }

    private static void DrawLabel(SKCanvas canvas, float x, float y, string text)
    {
        canvas.DrawText(text, x, y, LabelHalo);
        canvas.DrawText(text, x, y, LabelBody);
    }

    private static SKColor ColorFor(PoiKind kind) => kind switch
    {
        PoiKind.ExtractPmc => MarkerPalette.ExtractPmc,
        PoiKind.ExtractScav => MarkerPalette.ExtractScav,
        PoiKind.ExtractShared => MarkerPalette.ExtractShared,
        PoiKind.Transit => MarkerPalette.Transit,
        PoiKind.Spawn => MarkerPalette.Spawn,
        PoiKind.BossZone => MarkerPalette.BossZone,
        PoiKind.LootContainer => MarkerPalette.Loot,
        PoiKind.Hazard => MarkerPalette.Hazard,
        PoiKind.Lock => MarkerPalette.Lock,
        PoiKind.Switch => MarkerPalette.Switch,
        PoiKind.StationaryWeapon => MarkerPalette.StationaryWeapon,
        PoiKind.BtrStop => MarkerPalette.BtrStop,
        _ => SKColors.White,
    };
}
