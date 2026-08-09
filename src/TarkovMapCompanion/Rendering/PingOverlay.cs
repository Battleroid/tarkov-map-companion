using SkiaSharp;
using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Rendering;

/// <summary>A place somebody drew attention to, and when.</summary>
public sealed class MapPing
{
    public required string Name { get; init; }

    public required string Map { get; init; }

    public required GamePosition Position { get; init; }

    public required SKColor Color { get; init; }

    public DateTime PlacedUtc { get; init; } = DateTime.UtcNow;

    public double AgeSeconds => (DateTime.UtcNow - PlacedUtc).TotalSeconds;
}

/// <summary>
/// Short-lived "look here" marks from the squad.
/// </summary>
/// <remarks>
/// <para>
/// Unlike route markers, a ping is an event rather than a plan: it means something is true right
/// now, and it expires on its own so nobody has to tidy up mid-raid. Thirty seconds is long enough
/// to alt-tab and look, short enough that the map does not silt up over a raid.
/// </para>
/// <para>
/// Map-gated like peers, for the same reason -- a ping placed on Woods has no meaning in Customs
/// coordinates. It simply is not drawn.
/// </para>
/// </remarks>
public sealed class PingOverlay : IAnimatedOverlay
{
    /// <summary>How long a ping stays on the map.</summary>
    public const double LifetimeSeconds = 30.0;

    /// <summary>
    /// How long one ring takes to travel outward.
    /// </summary>
    /// <remarks>
    /// The rings used to stop after one pass, on the theory that the attention-grabbing part should
    /// be over before it became irritating. That had it backwards. A ping matters most to somebody
    /// who alt-tabs to the map fifteen seconds later and has to find it among three others, and by
    /// then the only thing left distinguishing it was a static diamond among static diamonds. So it
    /// radiates for its whole life, with the amplitude tapering as the ping ages -- it calms down
    /// instead of stopping, which keeps it findable without making it shout the entire time.
    /// </remarks>
    private const double RingPeriodSeconds = 1.6;

    private static readonly SKTypeface Typeface =
        SKTypeface.FromFamilyName("Cascadia Mono")
        ?? SKTypeface.FromFamilyName("Consolas")
        ?? SKTypeface.Default;

    private readonly object _gate = new();
    private readonly List<MapPing> _pings = [];

    /// <summary>Above the squad markers: a ping is the thing you are meant to look at.</summary>
    public int ZOrder => 960;

    public bool IsVisible { get; set; } = true;

    public GameMap? Map { get; set; }

    /// <summary>Whether anything is still on screen, so the repaint timer can stop.</summary>
    public bool Any
    {
        get { lock (_gate) return _pings.Count > 0; }
    }

    public void Add(MapPing ping)
    {
        lock (_gate)
        {
            Expire();
            _pings.Add(ping);
        }
    }

    public void Clear()
    {
        lock (_gate)
            _pings.Clear();
    }

    /// <summary>Drops anything past its lifetime. Returns true when something went.</summary>
    public bool Expire()
    {
        lock (_gate)
            return _pings.RemoveAll(p => p.AgeSeconds > LifetimeSeconds) > 0;
    }

    /// <summary>
    /// Retires expired pings, and keeps asking for frames while any remain.
    /// </summary>
    /// <remarks>
    /// A live ping always wants frames now that the rings run for its whole life. That costs
    /// nothing extra: the countdown in the label meant this overlay already needed repainting for
    /// all thirty seconds, so the old one-pass pulse was saving frames that were being drawn anyway.
    /// </remarks>
    public bool Advance()
    {
        Expire();
        return Any;
    }

    public void Draw(SKCanvas canvas, Viewport viewport)
    {
        if (Map is not { } map)
            return;

        MapPing[] pings;
        lock (_gate)
            pings = _pings.ToArray();

        foreach (var ping in pings)
        {
            if (!string.Equals(ping.Map, map.NormalizedName, StringComparison.OrdinalIgnoreCase))
                continue;

            DrawPing(canvas, viewport, map, ping);
        }
    }

    private static void DrawPing(SKCanvas canvas, Viewport viewport, GameMap map, MapPing ping)
    {
        var screen = viewport.ToScreen(map.ToBase(ping.Position));
        var x = (float)screen.X;
        var y = (float)screen.Y;

        var age = ping.AgeSeconds;

        // Holds full strength for most of its life and fades over the last third, so it goes
        // quietly rather than vanishing mid-glance.
        var remaining = Math.Clamp(1.0 - (age / LifetimeSeconds), 0.0, 1.0);
        var alpha = (byte)(255 * Math.Min(1.0, remaining / 0.35));

        // Two rings chasing each other outward, for as long as the ping lasts.
        for (var wave = 0; wave < 2; wave++)
        {
            var offset = wave * 0.5;
            var phase = ((age / RingPeriodSeconds) - offset) % 1.0;

            if (phase < 0)
                continue;

            using var ring = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.5f,

                // Tied to the ping's own fade as well as the ring's, so the rings go quiet on the
                // same schedule as the marker rather than pulsing brightly around something that
                // has almost expired.
                Color = ping.Color.WithAlpha((byte)(200 * (1.0 - phase) * (alpha / 255.0))),
            };

            // Shrinking reach as the ping ages: still unmistakably moving, less and less insistent.
            canvas.DrawCircle(x, y, (float)(8 + (phase * 34 * (0.45 + (0.55 * remaining)))), ring);
        }

        using var fill = new SKPaint { IsAntialias = true, Color = ping.Color.WithAlpha(alpha) };
        using var edge = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            Color = MarkerPalette.Halo.WithAlpha(alpha),
        };

        // A diamond outline with a dot, which reads as a marker rather than as a player.
        using (var path = new SKPath())
        {
            path.MoveTo(x, y - 9);
            path.LineTo(x + 9, y);
            path.LineTo(x, y + 9);
            path.LineTo(x - 9, y);
            path.Close();

            canvas.DrawPath(path, edge);
            canvas.DrawPath(path, fill);
        }

        DrawLabel(canvas, x + 13, y + 4, $"{ping.Name} · {Math.Max(0, LifetimeSeconds - age):F0}s", alpha);
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
