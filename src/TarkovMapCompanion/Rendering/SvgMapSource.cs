using SkiaSharp;
using Svg.Skia;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// Draws a map from tarkov.dev's vector artwork.
/// </summary>
/// <remarks>
/// <para>
/// Preferred over tiles wherever an SVG exists: one 300 KB file covers every zoom level, stays
/// crisp however far you push in, and carries named floor groups we can switch between.
/// </para>
/// <para>
/// Replaying the whole picture every frame is too slow for a 300 KB document, and pre-rasterizing
/// the entire map is not viable either -- Shoreline at 8x would be a 100 megapixel bitmap. So this
/// keeps a <em>viewport snapshot</em>: the visible region plus a margin, rasterized once at the
/// current scale and reused while panning stays inside it. Memory stays proportional to the window
/// rather than to the zoom level.
/// </para>
/// </remarks>
public sealed class SvgMapSource : IMapImageSource
{
    /// <summary>How much bigger than the viewport to rasterize, so small pans do not re-render.</summary>
    private const double SnapshotMargin = 0.35;

    /// <summary>Hard ceiling on snapshot pixels, to stay clear of surface allocation limits.</summary>
    private const int MaxSnapshotDimension = 8192;

    private readonly GameMap _map;
    private readonly AssetCache _assets;
    private readonly object _gate = new();

    private readonly Dictionary<string, SKPicture?> _picturesByLayerKey = new(StringComparer.Ordinal);

    private string? _svgText;
    private Task? _loadTask;

    private SKImage? _snapshot;
    private MapRect _snapshotRect;
    private double _snapshotScale;
    private string _snapshotLayerKey = "";

    private bool _disposed;

    public SvgMapSource(GameMap map, AssetCache assets)
    {
        _map = map;
        _assets = assets;
    }

    public string Name => "Vector";

    public bool IsReady
    {
        get { lock (_gate) return _svgText is not null; }
    }

    public event EventHandler? Invalidated;

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return _loadTask ??= LoadCoreAsync(cancellationToken);
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_map.SvgUrl))
            return;

        var text = await _assets.GetStringAsync(_map.SvgUrl, "svg", cancellationToken).ConfigureAwait(false);
        if (text is null)
            return;

        lock (_gate)
            _svgText = text;

        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    public void Draw(
        SKCanvas canvas,
        Viewport viewport,
        IReadOnlyCollection<string> activeFloorNames,
        bool includeBase = true)
    {
        var layerKey = BuildLayerKey(activeFloorNames, includeBase);

        var picture = GetPicture(layerKey);
        if (picture is null)
            return;

        var visible = viewport.VisibleBaseRect;

        if (!SnapshotCovers(layerKey, visible, viewport.Scale))
            RebuildSnapshot(layerKey, picture, visible, viewport);

        SKImage? snapshot;
        MapRect snapshotRect;
        lock (_gate)
        {
            snapshot = _snapshot;
            snapshotRect = _snapshotRect;
        }

        if (snapshot is null)
            return;

        var destination = viewport.ToScreen(snapshotRect);

        // Antialiasing does nothing for an axis-aligned image blit and is not free.
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = false };
        canvas.DrawImage(
            snapshot,
            new SKRect(
                (float)destination.Left,
                (float)destination.Top,
                (float)destination.Right,
                (float)destination.Bottom),
            paint);
    }

    /// <summary>
    /// Floor ids that should be present in the document, as a stable cache key. Named floors
    /// without an SVG group (tile-only floors, e.g. Customs' 4th) simply contribute nothing.
    /// </summary>
    private string BuildLayerKey(IReadOnlyCollection<string> activeFloorNames, bool includeBase)
    {
        var ids = _map.Floors
            .Where(floor => activeFloorNames.Contains(floor.Name) && floor.SvgLayerId is not null)
            .Select(floor => floor.SvgLayerId!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        // The base flag is part of the key: the same set of floors looks different with the
        // ground floor under it, so the two must not share a cached picture.
        return (includeBase ? "base|" : "nobase|") + string.Join('|', ids);
    }

    private SKPicture? GetPicture(string layerKey)
    {
        lock (_gate)
        {
            if (_svgText is null)
                return null;

            if (_picturesByLayerKey.TryGetValue(layerKey, out var cached))
                return cached;
        }

        string text;
        lock (_gate)
            text = _svgText!;

        // Key format is "<base|nobase>|<layer ids...>"; see BuildLayerKey.
        var parts = layerKey.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var includeBase = parts.Length == 0 || parts[0] != "nobase";
        var extras = parts.Skip(1).ToArray();

        SKPicture? picture = null;
        try
        {
            var filtered = SvgLayerFilter.Filter(text, _map.BaseSvgLayerId, extras, includeBase);

            var svg = new SKSvg();
            picture = svg.FromSvg(filtered);
        }
        catch (Exception ex)
        {
            // A map we cannot rasterize should leave the rest of the app working.
            Console.Error.WriteLine($"svg: failed to rasterize {_map.NormalizedName}: {ex.Message}");
        }

        lock (_gate)
            _picturesByLayerKey[layerKey] = picture;

        return picture;
    }

    private bool SnapshotCovers(string layerKey, MapRect visible, double scale)
    {
        lock (_gate)
        {
            if (_snapshot is null || !string.Equals(_snapshotLayerKey, layerKey, StringComparison.Ordinal))
                return false;

            // Re-rasterize on any real zoom change; scaling a snapshot up looks soft, and scaling
            // it down wastes the detail we already paid for.
            if (Math.Abs(Math.Log2(scale / _snapshotScale)) > 0.01)
                return false;

            return visible.Left >= _snapshotRect.Left
                && visible.Top >= _snapshotRect.Top
                && visible.Right <= _snapshotRect.Right
                && visible.Bottom <= _snapshotRect.Bottom;
        }
    }

    private void RebuildSnapshot(string layerKey, SKPicture picture, MapRect visible, Viewport viewport)
    {
        // Never rasterize beyond the map itself; at low zoom the visible rect is mostly empty space.
        var wanted = visible.Inflate(SnapshotMargin);
        var target = new MapRect(
            Math.Max(wanted.Left, _map.SvgBaseRect.Left),
            Math.Max(wanted.Top, _map.SvgBaseRect.Top),
            Math.Min(wanted.Right, _map.SvgBaseRect.Right),
            Math.Min(wanted.Bottom, _map.SvgBaseRect.Bottom));

        if (target.Width <= 0 || target.Height <= 0)
            return;

        var scale = viewport.Scale;
        var pixelWidth = (int)Math.Ceiling(target.Width * scale);
        var pixelHeight = (int)Math.Ceiling(target.Height * scale);

        if (pixelWidth <= 0 || pixelHeight <= 0)
            return;

        if (pixelWidth > MaxSnapshotDimension || pixelHeight > MaxSnapshotDimension)
        {
            var shrink = Math.Min(
                MaxSnapshotDimension / (double)pixelWidth,
                MaxSnapshotDimension / (double)pixelHeight);

            scale *= shrink;
            pixelWidth = Math.Max(1, (int)Math.Ceiling(target.Width * scale));
            pixelHeight = Math.Max(1, (int)Math.Ceiling(target.Height * scale));
        }

        SKImage image;
        try
        {
            var info = new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
                return;

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            // The picture draws in SVG user units; map those onto the SVG's base-space rect, then
            // into the snapshot's own pixel grid.
            var cullRect = picture.CullRect;
            if (cullRect.Width <= 0 || cullRect.Height <= 0)
                return;

            var svgRect = _map.SvgBaseRect;
            var unitsToBaseX = svgRect.Width / cullRect.Width;
            var unitsToBaseY = svgRect.Height / cullRect.Height;

            canvas.Scale((float)scale);
            canvas.Translate((float)(svgRect.Left - target.Left), (float)(svgRect.Top - target.Top));
            canvas.Scale((float)unitsToBaseX, (float)unitsToBaseY);
            canvas.Translate(-cullRect.Left, -cullRect.Top);

            canvas.DrawPicture(picture);
            canvas.Flush();

            image = surface.Snapshot();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"svg: snapshot failed for {_map.NormalizedName}: {ex.Message}");
            return;
        }

        lock (_gate)
        {
            _snapshot?.Dispose();
            _snapshot = image;
            _snapshotRect = target;
            _snapshotScale = scale;
            _snapshotLayerKey = layerKey;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_gate)
        {
            _snapshot?.Dispose();
            _snapshot = null;

            foreach (var picture in _picturesByLayerKey.Values)
                picture?.Dispose();

            _picturesByLayerKey.Clear();
        }
    }
}
