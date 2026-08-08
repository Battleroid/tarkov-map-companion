using SkiaSharp;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// Draws the guide line from the player to the selected extract, labeled with distance and how
/// far off the player's current heading it is.
/// </summary>
/// <remarks>
/// The bearing offset is the part that actually helps mid-raid: knowing the exit is 240 m away
/// matters less than knowing it is 30 degrees to your left.
/// </remarks>
public sealed class ExtractLineOverlay : IMapOverlay
{
    private static readonly SKTypeface Typeface =
        SKTypeface.FromFamilyName("Cascadia Mono")
        ?? SKTypeface.FromFamilyName("Consolas")
        ?? SKTypeface.Default;

    public int ZOrder => 900;

    public bool IsVisible { get; set; } = true;

    public GameMap? Map { get; set; }

    public MapPoi? Target { get; set; }

    /// <summary>Set by the session whenever a new fix arrives.</summary>
    public GamePosition? PlayerPosition { get; set; }

    /// <summary>Player's current facing, for the relative bearing readout.</summary>
    public double PlayerYawDegrees { get; set; }

    /// <summary>Straight-line ground distance to the target in meters, or null when there is none.</summary>
    public double? DistanceMeters =>
        Target is null || PlayerPosition is not { } player
            ? null
            : player.GroundDistanceTo(Target.Position);

    /// <summary>Degrees to turn to face the target: negative is left, positive is right.</summary>
    public double? RelativeBearingDegrees =>
        Target is null || PlayerPosition is not { } player
            ? null
            : MapProjection.NormalizeSigned(MapProjection.BearingDegrees(player, Target.Position) - PlayerYawDegrees);

    public void Draw(SKCanvas canvas, Viewport viewport)
    {
        if (Map is not { } map || Target is not { } target || PlayerPosition is not { } player)
            return;

        var from = viewport.ToScreen(map.ToBase(player));
        var to = viewport.ToScreen(target.Base);

        using var line = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            Color = MarkerPalette.ExtractLine.WithAlpha(0xCC),
            PathEffect = SKPathEffect.CreateDash([8f, 6f], 0),
        };

        canvas.DrawLine((float)from.X, (float)from.Y, (float)to.X, (float)to.Y, line);

        DrawReadout(canvas, from, to);
    }

    private void DrawReadout(SKCanvas canvas, MapPoint from, MapPoint to)
    {
        if (DistanceMeters is not { } distance)
            return;

        var text = $"{distance:F0} m";

        if (RelativeBearingDegrees is { } bearing)
        {
            // Under a few degrees, "dead ahead" is more useful than a jittering number.
            text += Math.Abs(bearing) < 4
                ? "  ahead"
                : $"  {Math.Abs(bearing):F0}° {(bearing < 0 ? "left" : "right")}";
        }

        var midX = (float)((from.X + to.X) / 2);
        var midY = (float)((from.Y + to.Y) / 2);

        using var halo = new SKPaint
        {
            IsAntialias = true,
            Typeface = Typeface,
            TextSize = 13,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3.5f,
            Color = MarkerPalette.Halo,
            TextAlign = SKTextAlign.Center,
        };
        using var body = new SKPaint
        {
            IsAntialias = true,
            Typeface = Typeface,
            TextSize = 13,
            Color = MarkerPalette.ExtractLine,
            TextAlign = SKTextAlign.Center,
        };

        canvas.DrawText(text, midX, midY - 6, halo);
        canvas.DrawText(text, midX, midY - 6, body);
    }
}
