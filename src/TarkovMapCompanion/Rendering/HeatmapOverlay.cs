using SkiaSharp;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Data.Models;
using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Rendering;

/// <summary>Which population a heatmap band represents.</summary>
public enum SpawnGroup
{
    /// <summary>Where human PMCs can start.</summary>
    Pmc,

    /// <summary>Where player and AI scavs can start.</summary>
    Scav,

    /// <summary>AI PMCs (raiders, rogues, scav-raiders).</summary>
    AiPmc,

    /// <summary>Boss and boss-escort zones.</summary>
    Boss,
}

/// <summary>
/// Density map of where players and AI can spawn.
/// </summary>
/// <remarks>
/// <para>
/// A scatter of a few hundred identical dots does not answer the question people actually have,
/// which is "which end of the map am I likely to get shot from in the first two minutes". Summing
/// overlapping falloffs does: clusters read as hot, isolated points barely register.
/// </para>
/// <para>
/// Two properties matter for it to be honest. The radius is specified in <em>game meters</em>, not
/// pixels, so zooming changes how much you can see rather than what the data says. And each group
/// is normalized against its own peak, so a map with 40 PMC spawns and 200 scav spawns does not
/// render the PMC layer invisible.
/// </para>
/// </remarks>
public sealed class HeatmapOverlay : IMapOverlay
{
    /// <summary>
    /// Resolution the density field is accumulated at. Coarse on purpose: the underlying data is
    /// a few hundred points, so a finer grid would cost time without adding information.
    /// </summary>
    private const int CellPixels = 4;

    /// <summary>Guard against absurd viewports allocating a huge field.</summary>
    private const int MaxCells = 512;

    private readonly Dictionary<SpawnGroup, List<GamePosition>> _points = [];

    private SKImage? _cached;
    private MapRect _cachedRect;
    private double _cachedScale;
    private double _cachedRadius;
    private int _cachedGroupMask;

    public int ZOrder => 200;

    public bool IsVisible { get; set; }

    public GameMap? Map { get; set; }

    /// <summary>Falloff radius in game meters. Bigger blurs clusters together.</summary>
    public double RadiusMeters { get; set; } = 40.0;

    public double Opacity { get; set; } = 0.55;

    /// <summary>Which groups contribute. Missing entries count as off.</summary>
    public IDictionary<SpawnGroup, bool> Groups { get; } = new Dictionary<SpawnGroup, bool>
    {
        [SpawnGroup.Pmc] = true,
        [SpawnGroup.Scav] = true,
        [SpawnGroup.AiPmc] = false,
        [SpawnGroup.Boss] = false,
    };

    public int PointCount(SpawnGroup group) => _points.TryGetValue(group, out var list) ? list.Count : 0;

    /// <summary>Extracts spawn positions from the raw map data, grouped by who spawns there.</summary>
    public void SetData(MapPoiData? data)
    {
        _points.Clear();
        Invalidate();

        if (data is null)
            return;

        foreach (var spawn in data.Spawns ?? [])
        {
            if (spawn.Position is null)
                continue;

            var position = new GamePosition(spawn.Position.X, spawn.Position.Y, spawn.Position.Z);

            foreach (var group in Classify(spawn))
            {
                if (!_points.TryGetValue(group, out var list))
                    _points[group] = list = [];

                list.Add(position);
            }
        }

        // Boss zones carry their own coordinates, so they do not need joining back to spawn zones.
        foreach (var boss in data.Bosses ?? [])
        {
            foreach (var location in boss.SpawnLocations ?? [])
            {
                foreach (var p in location.Positions ?? [])
                {
                    if (!_points.TryGetValue(SpawnGroup.Boss, out var list))
                        _points[SpawnGroup.Boss] = list = [];

                    list.Add(new GamePosition(p.X, p.Y, p.Z));
                }
            }
        }
    }

    /// <summary>
    /// A spawn can feed more than one band: a point flagged <c>player</c> and <c>bot</c> on the
    /// scav side is somewhere both a player scav and an AI scav can appear.
    /// </summary>
    private static IEnumerable<SpawnGroup> Classify(SpawnData spawn)
    {
        var categories = spawn.Categories ?? [];
        var sides = spawn.Sides ?? [];

        if (categories.Contains("boss"))
            yield return SpawnGroup.Boss;

        if (categories.Contains("botpmc"))
            yield return SpawnGroup.AiPmc;

        if (sides.Contains("pmc") && categories.Contains("player"))
            yield return SpawnGroup.Pmc;

        if (sides.Contains("scav"))
            yield return SpawnGroup.Scav;
    }

    public void Invalidate()
    {
        _cached?.Dispose();
        _cached = null;
    }

    public void Draw(SKCanvas canvas, Viewport viewport)
    {
        if (Map is null || _points.Count == 0)
            return;

        var mask = GroupMask();
        if (mask == 0)
            return;

        var visible = viewport.VisibleBaseRect;

        if (!IsCacheValid(mask, visible, viewport.Scale))
            Rebuild(mask, visible, viewport);

        if (_cached is null)
            return;

        var destination = viewport.ToScreen(_cachedRect);

        using var paint = new SKPaint
        {
            FilterQuality = SKFilterQuality.Medium,
            Color = SKColors.White.WithAlpha((byte)(Math.Clamp(Opacity, 0, 1) * 255)),
        };

        canvas.DrawImage(
            _cached,
            new SKRect(
                (float)destination.Left,
                (float)destination.Top,
                (float)destination.Right,
                (float)destination.Bottom),
            paint);
    }

    private int GroupMask()
    {
        var mask = 0;
        foreach (var group in Enum.GetValues<SpawnGroup>())
        {
            if (Groups.TryGetValue(group, out var on) && on && PointCount(group) > 0)
                mask |= 1 << (int)group;
        }

        return mask;
    }

    private bool IsCacheValid(int mask, MapRect visible, double scale) =>
        _cached is not null
        && mask == _cachedGroupMask
        && Math.Abs(_cachedRadius - RadiusMeters) < 1e-9
        && Math.Abs(Math.Log2(scale / _cachedScale)) < 0.2
        && visible.Left >= _cachedRect.Left
        && visible.Top >= _cachedRect.Top
        && visible.Right <= _cachedRect.Right
        && visible.Bottom <= _cachedRect.Bottom;

    private void Rebuild(int mask, MapRect visible, Viewport viewport)
    {
        var map = Map!;

        // Cover more than the viewport so panning does not rebuild constantly, but never more
        // than the map itself.
        var wanted = visible.Inflate(0.3);
        var target = new MapRect(
            Math.Max(wanted.Left, map.BaseRect.Left),
            Math.Max(wanted.Top, map.BaseRect.Top),
            Math.Min(wanted.Right, map.BaseRect.Right),
            Math.Min(wanted.Bottom, map.BaseRect.Bottom));

        if (target.Width <= 0 || target.Height <= 0)
            return;

        var scale = viewport.Scale;
        var cols = (int)Math.Ceiling(target.Width * scale / CellPixels);
        var rows = (int)Math.Ceiling(target.Height * scale / CellPixels);

        if (cols <= 0 || rows <= 0)
            return;

        // Shrink the field rather than the area, so zooming out keeps showing the whole map.
        if (cols > MaxCells || rows > MaxCells)
        {
            var shrink = Math.Min(MaxCells / (double)cols, MaxCells / (double)rows);
            cols = Math.Max(1, (int)(cols * shrink));
            rows = Math.Max(1, (int)(rows * shrink));
        }

        var cellWidth = target.Width / cols;
        var cellHeight = target.Height / rows;

        // Radius in base units. The map's own scale converts meters, so a 40 m radius covers the
        // same ground on Factory as on Streets.
        var radiusBase = RadiusMeters * map.Projection.AverageScale;
        var radiusCols = Math.Max(1, (int)Math.Ceiling(radiusBase / cellWidth));
        var radiusRows = Math.Max(1, (int)Math.Ceiling(radiusBase / cellHeight));

        var pixels = new SKColor[cols * rows];
        var field = new float[cols * rows];

        foreach (var group in Enum.GetValues<SpawnGroup>())
        {
            if ((mask & (1 << (int)group)) == 0)
                continue;

            Array.Clear(field);
            Accumulate(field, cols, rows, target, cellWidth, cellHeight, radiusBase, radiusCols, radiusRows, group);
            Composite(pixels, field, group);
        }

        var info = new SKImageInfo(cols, rows, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        bitmap.Pixels = pixels;

        _cached?.Dispose();
        _cached = SKImage.FromBitmap(bitmap);
        _cachedRect = target;
        _cachedScale = scale;
        _cachedRadius = RadiusMeters;
        _cachedGroupMask = mask;
    }

    private void Accumulate(
        float[] field,
        int cols,
        int rows,
        MapRect target,
        double cellWidth,
        double cellHeight,
        double radiusBase,
        int radiusCols,
        int radiusRows,
        SpawnGroup group)
    {
        if (!_points.TryGetValue(group, out var points))
            return;

        var map = Map!;
        var radiusSquared = radiusBase * radiusBase;

        foreach (var position in points)
        {
            var basePoint = map.ToBase(position);

            var centerCol = (int)((basePoint.X - target.Left) / cellWidth);
            var centerRow = (int)((basePoint.Y - target.Top) / cellHeight);

            var minCol = Math.Max(0, centerCol - radiusCols);
            var maxCol = Math.Min(cols - 1, centerCol + radiusCols);
            var minRow = Math.Max(0, centerRow - radiusRows);
            var maxRow = Math.Min(rows - 1, centerRow + radiusRows);

            for (var row = minRow; row <= maxRow; row++)
            {
                var y = target.Top + (row + 0.5) * cellHeight - basePoint.Y;
                var ySquared = y * y;

                for (var col = minCol; col <= maxCol; col++)
                {
                    var x = target.Left + (col + 0.5) * cellWidth - basePoint.X;
                    var distanceSquared = x * x + ySquared;

                    if (distanceSquared >= radiusSquared)
                        continue;

                    // Smooth falloff to zero at the radius, so blobs blend instead of tiling.
                    var t = 1.0 - distanceSquared / radiusSquared;
                    field[row * cols + col] += (float)(t * t);
                }
            }
        }
    }

    /// <summary>
    /// Colorises one group's field and blends it over what is already there. Each group is
    /// normalized to its own maximum so sparse groups stay visible next to dense ones.
    /// </summary>
    private static void Composite(SKColor[] pixels, float[] field, SpawnGroup group)
    {
        var peak = 0f;
        foreach (var value in field)
        {
            if (value > peak)
                peak = value;
        }

        if (peak <= 0)
            return;

        var tint = TintFor(group);

        for (var i = 0; i < field.Length; i++)
        {
            var intensity = field[i] / peak;
            if (intensity <= 0.02f)
                continue;

            // Ramp alpha faster than intensity so the hot cores read clearly.
            var alpha = (byte)(Math.Clamp(Math.Sqrt(intensity), 0, 1) * 210);

            var existing = pixels[i];
            if (existing.Alpha == 0)
            {
                pixels[i] = tint.WithAlpha(alpha);
                continue;
            }

            // Screen-style blend: overlapping groups brighten rather than one hiding the other.
            pixels[i] = new SKColor(
                (byte)Math.Max(existing.Red, tint.Red * alpha / 255),
                (byte)Math.Max(existing.Green, tint.Green * alpha / 255),
                (byte)Math.Max(existing.Blue, tint.Blue * alpha / 255),
                Math.Max(existing.Alpha, alpha));
        }
    }

    /// <summary>
    /// Distinct in hue and lightness so overlapping bands stay separable, including for the most
    /// common color-vision deficiencies.
    /// </summary>
    private static SKColor TintFor(SpawnGroup group) => group switch
    {
        SpawnGroup.Pmc => new SKColor(0xFF, 0x45, 0x45),
        SpawnGroup.Scav => new SKColor(0x36, 0xA2, 0xFF),
        SpawnGroup.AiPmc => new SKColor(0xC9, 0x63, 0xFF),
        SpawnGroup.Boss => new SKColor(0xFF, 0xC4, 0x2E),
        _ => SKColors.White,
    };
}
