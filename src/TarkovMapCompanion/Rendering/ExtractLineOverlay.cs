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

    /// <summary>
    /// Color of the line when it points at an exit. The waypoint case is not configurable.
    /// </summary>
    /// <remarks>
    /// Deliberately asymmetric. The exit color is a preference; the waypoint color is a signal that
    /// the line has switched to routing you along your own marks, and letting someone set the two
    /// the same would remove the one cue that distinguishes them.
    /// </remarks>
    public SKColor Color { get; set; } = MarkerPalette.ExtractLine;

    /// <summary>
    /// The next waypoint on the player's own route. Takes precedence over <see cref="Target"/>.
    /// </summary>
    /// <remarks>
    /// A route the player drew is a statement of where they want to go next, which is strictly
    /// more specific than the exit they eventually mean to leave by. The exit is still selected and
    /// still drawn; the line just points at the nearer commitment until the route is done or
    /// cleared.
    /// </remarks>
    public Waypoint? Waypoint { get; set; }

    /// <summary>Set by the session whenever a new fix arrives.</summary>
    public GamePosition? PlayerPosition { get; set; }

    /// <summary>Player's current facing, for the relative bearing readout.</summary>
    public double PlayerYawDegrees { get; set; }

    /// <summary>Where the line actually points, waypoint first.</summary>
    private (GamePosition Position, MapPoint Base, string? Label)? Guide =>
        Waypoint is { } waypoint ? (waypoint.Position, waypoint.Base, $"#{waypoint.Number}")
        : Target is { } target ? (target.Position, target.Base, null)
        : null;

    /// <summary>Base-space point being navigated to, for framing the view on it.</summary>
    public MapPoint? GuideBase => Guide?.Base;

    /// <summary>Straight-line ground distance to the target in meters, or null when there is none.</summary>
    public double? DistanceMeters =>
        Guide is not { } guide || PlayerPosition is not { } player
            ? null
            : player.GroundDistanceTo(guide.Position);

    /// <summary>Degrees to turn to face the target: negative is left, positive is right.</summary>
    public double? RelativeBearingDegrees =>
        Guide is not { } guide || PlayerPosition is not { } player
            ? null
            : MapProjection.NormalizeSigned(MapProjection.BearingDegrees(player, guide.Position) - PlayerYawDegrees);

    public void Draw(SKCanvas canvas, Viewport viewport)
    {
        if (Map is not { } map || Guide is not { } target || PlayerPosition is not { } player)
            return;

        var from = viewport.ToScreen(map.ToBase(player));
        var to = viewport.ToScreen(target.Base);

        // Colored by what it is pointing at, so "am I being routed to a pin or to the exit" is
        // answerable without reading the label.
        var color = Waypoint is null ? Color : MarkerPalette.Waypoint;

        // Solid and heavy. This is the one line on the map you are meant to follow while glancing
        // at a second monitor, and a dashed 2px stroke lost that argument against the map artwork
        // at anything below full zoom. Alpha stays at 0xCC -- at 3px, solid is already far more ink
        // than the dashes were, and taking it to full opacity as well would swamp what is beneath.
        using var line = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            StrokeCap = SKStrokeCap.Round,
            Color = color.WithAlpha(0xCC),
        };

        canvas.DrawLine((float)from.X, (float)from.Y, (float)to.X, (float)to.Y, line);

        DrawReadout(canvas, from, to, target.Label, color);
    }

    private void DrawReadout(SKCanvas canvas, MapPoint from, MapPoint to, string? label, SKColor color)
    {
        if (DistanceMeters is not { } distance)
            return;

        var text = label is null ? $"{distance:F0} m" : $"{label}  {distance:F0} m";

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
            Color = color,
            TextAlign = SKTextAlign.Center,
        };

        canvas.DrawText(text, midX, midY - 6, halo);
        canvas.DrawText(text, midX, midY - 6, body);
    }
}
