using SkiaSharp;
using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// The place names drawn in the map artwork, as a layer: white text with a dark halo, exactly the
/// styling tarkov.dev uses for its place-names layer.
/// </summary>
/// <remarks>
/// <para>
/// The names come from the map catalog's <c>labels[]</c> (<see cref="GameMap.Labels"/>), placed with
/// the same projection every marker uses. They are always English and are drawn straight from
/// <see cref="MapLabelJson.Text"/>: this is place data, not UI text, so it must never pass through
/// the localizer.
/// </para>
/// <para>
/// A label's <c>top</c>/<c>bottom</c> are a game-world height band (which floors it belongs to), not
/// a pixel nudge, so the overlay ignores them and positions purely from <c>[x, z]</c>. <c>size</c>
/// is a percentage with 100 as the baseline; <c>rotation</c> is degrees about the anchor.
/// </para>
/// </remarks>
public sealed class LandmarkOverlay : IMapOverlay
{
    /// <summary>Font size for a label with <c>size: 100</c> (or no size at all).</summary>
    private const float BaseTextSize = 12f;

    /// <summary>
    /// Above the base imagery and the heatmap, but below the POI, annotation and quest markers, the
    /// guide line and the player marker. Names are background context, so the markers that answer
    /// "what is here" and "where am I going" all draw over them.
    /// </summary>
    public int ZOrder => 400;

    public bool IsVisible { get; set; } = true;

    public GameMap Map { get; set; }

    public LandmarkOverlay(GameMap map) => Map = map;

    public void Draw(SKCanvas canvas, Viewport viewport)
    {
        var typeface = AppFonts.Label;

        using var text = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            Typeface = typeface,
        };
        using var halo = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 220),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3,
            Typeface = typeface,
        };

        foreach (var label in Map.Labels)
        {
            if (label.Position is not { Count: >= 2 } position || string.IsNullOrEmpty(label.Text))
                continue;

            var screen = viewport.ToScreen(Map.Projection.ToBase(position[0], position[1]));

            // size is a percentage, with 100 (or a missing value) as the baseline.
            var textSize = BaseTextSize * (float)((label.Size ?? 100) / 100.0);
            text.TextSize = textSize;
            halo.TextSize = textSize;

            // Centre the text on its anchor, then rotate about the anchor so a rotated name spins
            // in place instead of orbiting the point.
            var dx = -text.MeasureText(label.Text) / 2f;
            var dy = textSize * 0.35f;

            canvas.Save();
            canvas.Translate((float)screen.X, (float)screen.Y);

            if (label.Rotation is { } rotation)
                canvas.RotateDegrees((float)rotation);

            canvas.DrawText(label.Text, dx, dy, halo);
            canvas.DrawText(label.Text, dx, dy, text);
            canvas.Restore();
        }
    }
}
