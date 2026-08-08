using SkiaSharp;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Party;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// Draws the rest of the squad.
/// </summary>
/// <remarks>
/// <para>
/// Two rules decide everything here, and both are about not lying.
/// </para>
/// <para>
/// A peer is drawn only when they are on the map you are looking at. Positions from another map are
/// meaningless in this one's coordinates, and drawing them would scatter teammates across a raid
/// they are not in. They stay in the roster list, labelled with where they actually are.
/// </para>
/// <para>
/// And a marker fades with age. Peers only report when they take a screenshot, so a dot can easily
/// be a minute old. Drawn at full strength it reads as "he is there now", which is how you end up
/// trusting an angle nobody is covering any more. Every marker carries how old it is, and old ones
/// recede.
/// </para>
/// </remarks>
public sealed class PeerOverlay : IMapOverlay
{
    private static readonly SKTypeface Typeface =
        SKTypeface.FromFamilyName("Cascadia Mono")
        ?? SKTypeface.FromFamilyName("Consolas")
        ?? SKTypeface.Default;

    /// <summary>Past this, a position says more about where someone was than where they are.</summary>
    private const double FullyStaleSeconds = 180.0;

    private const float Radius = 7f;

    private IReadOnlyList<PartyPeer> _peers = [];

    public int ZOrder => 950;

    public bool IsVisible { get; set; } = true;

    public GameMap? Map { get; set; }

    public void SetPeers(IReadOnlyList<PartyPeer> peers) => _peers = peers;

    /// <summary>Peers worth drawing: placed, not us, and in this raid's map.</summary>
    public IEnumerable<PartyPeer> Drawable =>
        Map is not { } map
            ? []
            : _peers.Where(p =>
                !p.IsSelf
                && p.HasPosition
                && string.Equals(p.Map, map.NormalizedName, StringComparison.OrdinalIgnoreCase));

    public void Draw(SKCanvas canvas, Viewport viewport)
    {
        if (Map is not { } map)
            return;

        foreach (var peer in Drawable)
            DrawPeer(canvas, viewport, map, peer, ColorFor(peer.Name));
    }

    /// <summary>
    /// The color a squad member is drawn in, by their place in the roster.
    /// </summary>
    /// <remarks>
    /// Exposed so the roster list can show the same swatch as the map. A name in a list is not much
    /// use if you cannot tell which wedge on the map it belongs to.
    /// </remarks>
    public SKColor ColorFor(string name)
    {
        var index = 0;

        foreach (var peer in _peers)
        {
            if (peer.IsSelf)
                continue;

            if (string.Equals(peer.Name, name, StringComparison.OrdinalIgnoreCase))
                return MarkerPalette.PeerColors[index % MarkerPalette.PeerColors.Length];

            index++;
        }

        return MarkerPalette.PeerColors[0];
    }

    private static void DrawPeer(SKCanvas canvas, Viewport viewport, GameMap map, PartyPeer peer, SKColor color)
    {
        var screen = viewport.ToScreen(map.ToBase(peer.Position));
        var x = (float)screen.X;
        var y = (float)screen.Y;

        // Full strength when fresh, down to a third when the position is old enough to be history.
        var staleness = Math.Clamp(peer.AgeSeconds / FullyStaleSeconds, 0.0, 1.0);
        var alpha = (byte)(255 - (staleness * 170));

        using var fill = new SKPaint { IsAntialias = true, Color = color.WithAlpha(alpha) };
        using var edge = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.6f,
            Color = MarkerPalette.Halo.WithAlpha(alpha),
        };

        // A wedge pointing where they were facing, so it reads differently from your own arrow
        // at a glance while still carrying the heading.
        var heading = (float)map.Projection.ScreenAngleDegrees(peer.Yaw);

        canvas.Save();
        canvas.Translate(x, y);
        canvas.RotateDegrees(heading);

        using (var path = new SKPath())
        {
            path.MoveTo(0, -Radius - 3f);
            path.LineTo(Radius, Radius);
            path.LineTo(0, Radius * 0.45f);
            path.LineTo(-Radius, Radius);
            path.Close();

            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, edge);
        }

        canvas.Restore();

        DrawLabel(canvas, x + Radius + 5, y + 4, Describe(peer), alpha);
    }

    /// <summary>Name plus age, because a name on its own implies the position is current.</summary>
    private static string Describe(PartyPeer peer)
    {
        var age = peer.AgeSeconds;

        var when = age switch
        {
            < 20 => "now",
            < 90 => $"{age:F0}s",
            _ => $"{age / 60:F0}m",
        };

        return $"{peer.Name} · {when}";
    }

    private static void DrawLabel(SKCanvas canvas, float x, float y, string text, byte alpha)
    {
        using var halo = new SKPaint
        {
            IsAntialias = true,
            Typeface = Typeface,
            TextSize = 12,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3,
            Color = MarkerPalette.Halo.WithAlpha(alpha),
        };

        using var body = new SKPaint
        {
            IsAntialias = true,
            Typeface = Typeface,
            TextSize = 12,
            Color = MarkerPalette.LabelText.WithAlpha(alpha),
        };

        canvas.DrawText(text, x, y, halo);
        canvas.DrawText(text, x, y, body);
    }

}
