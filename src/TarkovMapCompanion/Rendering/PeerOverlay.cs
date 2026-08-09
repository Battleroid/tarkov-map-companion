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
/// they are not in. They stay in the roster list, labeled with where they actually are.
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

    /// <summary>How far inside the viewport edge an off-screen arrow and its label sit.</summary>
    private const float EdgePadding = 26f;

    /// <summary>
    /// How far a teammate has to move before it counts as a new trail point, in meters.
    /// </summary>
    /// <remarks>
    /// The host broadcasts the whole roster every time anybody publishes, so SetPeers fires far more
    /// often than any one peer actually moves -- without a gate, somebody standing still would fill
    /// their own trail with copies of one spot. Spacing it out is also what makes five peer dots
    /// cover more ground than your own twelve: theirs is a "which way has he been drifting" cue
    /// meant to read at a glance from across the map, not a footstep log.
    /// </remarks>
    private const double MinTrailSpacingMeters = 25.0;

    /// <summary>Where one teammate has been on this map, oldest first.</summary>
    private sealed class PeerTrack
    {
        public readonly List<GamePosition> Points = [];
        public string Map = "";
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, PeerTrack> _tracks = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<PartyPeer> _peers = [];
    private GameMap? _map;

    public int ZOrder => 950;

    public bool IsVisible { get; set; } = true;

    public GameMap? Map
    {
        get => _map;
        set
        {
            // A trail is in one map's coordinates and means nothing in another's. MapSession already
            // assigns this on every switch, so clearing here is all the wiring map changes need.
            if (!ReferenceEquals(_map, value))
                ClearTrails();

            _map = value;
        }
    }

    /// <summary>How many past positions to keep per teammate. 0 turns peer trails off.</summary>
    public int TrailLength { get; set; } = 5;

    /// <summary>Point an arrow at teammates who are outside the viewport.</summary>
    public bool ShowOffScreen { get; set; } = true;

    /// <summary>The player's own position, for the distance on an off-screen arrow's label.</summary>
    public GamePosition? PlayerPosition { get; set; }

    public void SetPeers(IReadOnlyList<PartyPeer> peers)
    {
        _peers = peers;
        RecordPositions(peers);
    }

    /// <summary>Drops every trail. Called on a map change and when a session ends.</summary>
    public void ClearTrails()
    {
        lock (_gate)
            _tracks.Clear();
    }

    /// <summary>
    /// Folds the current roster into the per-peer trails.
    /// </summary>
    /// <remarks>
    /// Also prunes anyone who has left, which is what makes an ended session tidy up after itself:
    /// leaving empties the roster and raises Changed, so this runs with nothing to keep.
    /// </remarks>
    private void RecordPositions(IReadOnlyList<PartyPeer> peers)
    {
        lock (_gate)
        {
            var present = new HashSet<string>(
                peers.Where(p => !p.IsSelf).Select(p => p.Name),
                StringComparer.OrdinalIgnoreCase);

            foreach (var gone in _tracks.Keys.Where(k => !present.Contains(k)).ToArray())
                _tracks.Remove(gone);

            if (TrailLength <= 0)
            {
                _tracks.Clear();
                return;
            }

            foreach (var peer in peers)
            {
                if (peer.IsSelf || !peer.HasPosition)
                    continue;

                if (!_tracks.TryGetValue(peer.Name, out var track))
                    _tracks[peer.Name] = track = new PeerTrack { Map = peer.Map };

                // A transit means the old points describe somewhere else entirely.
                if (!string.Equals(track.Map, peer.Map, StringComparison.OrdinalIgnoreCase))
                {
                    track.Points.Clear();
                    track.Map = peer.Map;
                }

                var last = track.Points.Count > 0 ? track.Points[^1] : (GamePosition?)null;

                if (last is { } previous && previous.GroundDistanceTo(peer.Position) < MinTrailSpacingMeters)
                    continue;

                track.Points.Add(peer.Position);

                while (track.Points.Count > TrailLength)
                    track.Points.RemoveAt(0);
            }
        }
    }

    private GamePosition[] TrackFor(string name, string map)
    {
        lock (_gate)
        {
            return _tracks.TryGetValue(name, out var track)
                   && string.Equals(track.Map, map, StringComparison.OrdinalIgnoreCase)
                ? track.Points.ToArray()
                : [];
        }
    }

    /// <summary>Test seam: the recorded trail for one peer.</summary>
    internal GamePosition[] TrackForTests(string name, string map = "customs") => TrackFor(name, map);

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
        {
            var color = ColorFor(peer.Name);
            var screen = viewport.ToScreen(map.ToBase(peer.Position));

            // Full strength when fresh, down to a third once the position is old enough to be
            // history rather than news.
            var staleness = Math.Clamp(peer.AgeSeconds / FullyStaleSeconds, 0.0, 1.0);
            var alpha = (byte)(255 - (staleness * 170));

            var onScreen = screen.X >= 0 && screen.Y >= 0
                           && screen.X <= viewport.Width && screen.Y <= viewport.Height;

            if (onScreen)
            {
                DrawTrail(canvas, viewport, map, peer, color, alpha);
                DrawPeer(canvas, (float)screen.X, (float)screen.Y, map, peer, color, alpha);
                continue;
            }

            // Pointing insistently at where somebody was three minutes ago is exactly the lie this
            // overlay is written against, so a fully stale peer gets no arrow at all.
            if (ShowOffScreen && staleness < 1.0)
                DrawOffScreenArrow(canvas, viewport, screen.X, screen.Y, peer, color, alpha);
        }
    }

    /// <summary>Where a teammate has been, thinner and fainter than your own trail.</summary>
    private void DrawTrail(
        SKCanvas canvas, Viewport viewport, GameMap map, PartyPeer peer, SKColor color, byte alpha)
    {
        var points = TrackFor(peer.Name, peer.Map);
        if (points.Length < 2)
            return;

        using var line = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.8f,
            StrokeCap = SKStrokeCap.Round,
        };

        for (var i = 1; i < points.Length; i++)
        {
            var from = viewport.ToScreen(map.ToBase(points[i - 1]));
            var to = viewport.ToScreen(map.ToBase(points[i]));

            // Ramped by position in the trail, then scaled by the peer's own staleness, so an old
            // teammate's history does not come out brighter than their current marker.
            var age = (double)i / points.Length;
            line.Color = color.WithAlpha((byte)((0x28 + (0x68 * age)) * (alpha / 255.0)));

            canvas.DrawLine((float)from.X, (float)from.Y, (float)to.X, (float)to.Y, line);
        }
    }

    /// <summary>
    /// An arrow at the edge of the view for a teammate outside it.
    /// </summary>
    /// <remarks>
    /// The arrow is placed where the line from the center of the view to the peer crosses an inset
    /// rectangle, rather than by clamping the point into that rectangle. A clamp parks everybody
    /// diagonal in the same corner, which is worse than useless when two teammates are off in
    /// genuinely different directions.
    /// </remarks>
    private void DrawOffScreenArrow(
        SKCanvas canvas, Viewport viewport, double px, double py, PartyPeer peer, SKColor color, byte alpha)
    {
        var cx = viewport.Width / 2.0;
        var cy = viewport.Height / 2.0;

        var dx = px - cx;
        var dy = py - cy;

        if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6)
            return;

        var limitX = Math.Max(1.0, cx - EdgePadding);
        var limitY = Math.Max(1.0, cy - EdgePadding);

        var scale = Math.Min(
            Math.Abs(dx) < 1e-6 ? double.MaxValue : limitX / Math.Abs(dx),
            Math.Abs(dy) < 1e-6 ? double.MaxValue : limitY / Math.Abs(dy));

        var x = (float)(cx + (dx * scale));
        var y = (float)(cy + (dy * scale));
        var angle = Math.Atan2(dy, dx);

        using var fill = new SKPaint { IsAntialias = true, Color = color.WithAlpha(alpha) };
        using var edge = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.6f,
            Color = MarkerPalette.Halo.WithAlpha(alpha),
        };

        canvas.Save();
        canvas.Translate(x, y);
        canvas.RotateRadians((float)angle);

        using (var arrow = new SKPath())
        {
            arrow.MoveTo(10f, 0f);
            arrow.LineTo(-6f, -7f);
            arrow.LineTo(-3f, 0f);
            arrow.LineTo(-6f, 7f);
            arrow.Close();

            canvas.DrawPath(arrow, edge);
            canvas.DrawPath(arrow, fill);
        }

        canvas.Restore();

        var label = PlayerPosition is { } player
            ? $"{peer.Name} {player.GroundDistanceTo(peer.Position):F0} m"
            : peer.Name;

        // Placed back toward the middle of the view, and clamped so it never runs off the same edge
        // the arrow is pinned to.
        using var text = new SKPaint { IsAntialias = true, TextSize = 11, Typeface = Typeface };
        var width = text.MeasureText(label);

        var labelX = (float)Math.Clamp(x - (dx * scale * 0.06) - (width / 2.0), 4.0, viewport.Width - width - 4.0);
        var labelY = (float)Math.Clamp(y - (dy * scale * 0.06) + 4.0, 14.0, viewport.Height - 4.0);

        DrawLabel(canvas, labelX, labelY, label, alpha);
    }

    /// <summary>
    /// The color a squad member is drawn in: their own choice, or their roster slot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exposed so the roster list can show the same swatch as the map. A name in a list is not much
    /// use if you cannot tell which wedge on the map it belongs to.
    /// </para>
    /// <para>
    /// A declared color always wins, and alpha is forced opaque because this string came off a
    /// socket from a program we did not write. The index fallback below is only stable while nobody
    /// leaves: the walk skips self and enumerates in name order, so one person quitting recolors
    /// everybody after them, and two clients holding slightly different rosters draw the same
    /// teammate two different colors. Which is exactly the confusion the colors exist to prevent,
    /// and exactly why they are now sent rather than worked out.
    /// </para>
    /// </remarks>
    public SKColor ColorFor(string name)
    {
        var index = 0;

        foreach (var peer in _peers)
        {
            var match = string.Equals(peer.Name, name, StringComparison.OrdinalIgnoreCase);

            if (match && peer.Color is { } declared && ColorCodec.TryParse(declared, out var chosen))
                return chosen;

            if (peer.IsSelf)
                continue;

            if (match)
                return MarkerPalette.PeerColors[index % MarkerPalette.PeerColors.Length];

            index++;
        }

        return MarkerPalette.PeerColors[0];
    }

    private static void DrawPeer(
        SKCanvas canvas, float x, float y, GameMap map, PartyPeer peer, SKColor color, byte alpha)
    {
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
