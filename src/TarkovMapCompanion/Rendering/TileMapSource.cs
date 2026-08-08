using System.Collections.Concurrent;
using SkiaSharp;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// Draws a map from tarkov.dev's raster tile pyramids.
/// </summary>
/// <remarks>
/// <para>
/// Needed because The Lab, The Labyrinth and Icebreaker ship no vector artwork at all. It also
/// gives the seven dual-source maps a photographic alternative to the abstract SVG.
/// </para>
/// <para>
/// Stitching a pyramid into one image is not an option: The Labyrinth at its maximum zoom is
/// roughly 3,500 tiles and 240 megapixels. So tiles are fetched on demand for the visible region
/// at the zoom level closest to the current scale, cached in memory and on disk, and drawn as
/// they arrive. Whatever is already resident is drawn immediately, and coarser tiles stand in for
/// missing finer ones so zooming never shows a hole.
/// </para>
/// <para>
/// The tile grid derives from the same affine as everything else: Leaflet's pixel space at integer
/// zoom <c>z</c> is the base space scaled by 2^z, and tile (x, y) covers a
/// <c>tileSize</c>-square cell of it.
/// </para>
/// </remarks>
public sealed class TileMapSource : IMapImageSource
{
    /// <summary>Cap on decoded tiles held in memory. At 256px RGBA that is roughly 100 MB.</summary>
    private const int MaxResidentTiles = 400;

    /// <summary>Levels to search upward for a stand-in when a tile has not arrived yet.</summary>
    private const int FallbackLevels = 4;

    private readonly GameMap _map;
    private readonly AssetCache _assets;

    private readonly ConcurrentDictionary<TileKey, SKImage?> _tiles = new();
    private readonly ConcurrentDictionary<TileKey, byte> _inFlight = new();
    private readonly ConcurrentQueue<TileKey> _insertionOrder = new();

    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public TileMapSource(GameMap map, AssetCache assets)
    {
        _map = map;
        _assets = assets;
    }

    public string Name => "Satellite";

    /// <summary>Tiles stream in, so there is nothing to wait for before the first draw.</summary>
    public bool IsReady => !string.IsNullOrWhiteSpace(_map.BaseTilePathTemplate);

    public event EventHandler? Invalidated;

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Draw(SKCanvas canvas, Viewport viewport, IReadOnlyCollection<string> activeFloorNames)
    {
        var template = ResolveTemplate(activeFloorNames);
        if (template is null)
            return;

        var level = ChooseLevel(viewport.Scale);
        var visible = viewport.VisibleBaseRect;

        // Pixel space at this level, and the tile cells covering the visible rect.
        var levelScale = Math.Pow(2, level);
        var tileSize = _map.TileSize;

        var minTileX = (int)Math.Floor(visible.Left * levelScale / tileSize);
        var maxTileX = (int)Math.Floor(visible.Right * levelScale / tileSize);
        var minTileY = (int)Math.Floor(visible.Top * levelScale / tileSize);
        var maxTileY = (int)Math.Floor(visible.Bottom * levelScale / tileSize);

        // A pathological zoom-out should not queue thousands of requests.
        const int maxTilesPerAxis = 64;
        if (maxTileX - minTileX > maxTilesPerAxis || maxTileY - minTileY > maxTilesPerAxis)
            return;

        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = false };

        for (var tileY = minTileY; tileY <= maxTileY; tileY++)
        {
            for (var tileX = minTileX; tileX <= maxTileX; tileX++)
            {
                var key = new TileKey(template, level, tileX, tileY);
                var destination = TileDestination(viewport, level, tileX, tileY);

                if (_tiles.TryGetValue(key, out var image) && image is not null)
                {
                    canvas.DrawImage(image, destination, paint);
                    continue;
                }

                if (!_tiles.ContainsKey(key))
                    QueueFetch(key);

                DrawCoarseFallback(canvas, viewport, template, level, tileX, tileY, destination, paint);
            }
        }
    }

    /// <summary>
    /// Draws a slice of an already-loaded lower-resolution tile in place of a missing one, so
    /// panning and zooming degrade in sharpness rather than flashing empty cells.
    /// </summary>
    private void DrawCoarseFallback(
        SKCanvas canvas,
        Viewport viewport,
        string template,
        int level,
        int tileX,
        int tileY,
        SKRect destination,
        SKPaint paint)
    {
        for (var step = 1; step <= FallbackLevels; step++)
        {
            var coarseLevel = level - step;
            if (coarseLevel < _map.MinZoomLevel)
                return;

            var factor = 1 << step;
            var coarseX = (int)Math.Floor(tileX / (double)factor);
            var coarseY = (int)Math.Floor(tileY / (double)factor);

            if (!_tiles.TryGetValue(new TileKey(template, coarseLevel, coarseX, coarseY), out var coarse)
                || coarse is null)
            {
                continue;
            }

            // The sub-rect of the coarse tile that corresponds to this cell.
            var cell = _map.TileSize / (float)factor;
            var offsetX = (tileX - coarseX * factor) * cell;
            var offsetY = (tileY - coarseY * factor) * cell;

            canvas.DrawImage(
                coarse,
                new SKRect(offsetX, offsetY, offsetX + cell, offsetY + cell),
                destination,
                paint);
            return;
        }
    }

    private SKRect TileDestination(Viewport viewport, int level, int tileX, int tileY)
    {
        var levelScale = Math.Pow(2, level);
        var tileSize = _map.TileSize;

        var baseLeft = tileX * tileSize / levelScale;
        var baseTop = tileY * tileSize / levelScale;
        var baseRight = (tileX + 1) * tileSize / levelScale;
        var baseBottom = (tileY + 1) * tileSize / levelScale;

        var topLeft = viewport.ToScreen(new MapPoint(baseLeft, baseTop));
        var bottomRight = viewport.ToScreen(new MapPoint(baseRight, baseBottom));

        // Round outward by a hair: adjacent tiles otherwise leave hairline seams at some scales.
        return new SKRect(
            (float)Math.Floor(topLeft.X),
            (float)Math.Floor(topLeft.Y),
            (float)Math.Ceiling(bottomRight.X),
            (float)Math.Ceiling(bottomRight.Y));
    }

    /// <summary>
    /// Integer pyramid level whose native resolution is closest to the current scale. Base space
    /// is level 0, so a scale of 2^z means level z pixels map one-to-one onto screen pixels.
    /// </summary>
    private int ChooseLevel(double scale)
    {
        var ideal = Math.Log2(Math.Max(scale, 1e-6));
        return Math.Clamp((int)Math.Round(ideal), _map.MinZoomLevel, _map.MaxZoomLevel);
    }

    /// <summary>
    /// The tile set to draw. A selected floor with its own pyramid replaces the base imagery,
    /// matching how tarkov.dev swaps rather than stacks raster floors.
    /// </summary>
    private string? ResolveTemplate(IReadOnlyCollection<string> activeFloorNames)
    {
        foreach (var floor in _map.Floors)
        {
            if (floor.TilePathTemplate is not null && activeFloorNames.Contains(floor.Name))
                return floor.TilePathTemplate;
        }

        return _map.BaseTilePathTemplate;
    }

    private void QueueFetch(TileKey key)
    {
        if (!_inFlight.TryAdd(key, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var url = key.Template
                    .Replace("{z}", key.Level.ToString())
                    .Replace("{x}", key.X.ToString())
                    .Replace("{y}", key.Y.ToString());

                var bytes = await _assets
                    .GetAsync(url, Path.Combine("tiles", _map.NormalizedName), _shutdown.Token)
                    .ConfigureAwait(false);

                SKImage? image = null;
                if (bytes is { Length: > 0 })
                {
                    // A 404 for an out-of-range tile is normal at the pyramid edges; a null entry
                    // records "asked and there is nothing here" so we never ask again.
                    using var data = SKData.CreateCopy(bytes);
                    image = SKImage.FromEncodedData(data);
                }

                Store(key, image);

                if (image is not null)
                    Invalidated?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"tiles: {key.Level}/{key.X}/{key.Y}: {ex.Message}");
                Store(key, null);
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
            }
        }, _shutdown.Token);
    }

    private void Store(TileKey key, SKImage? image)
    {
        if (_disposed)
        {
            image?.Dispose();
            return;
        }

        _tiles[key] = image;
        _insertionOrder.Enqueue(key);
        TrimResident();
    }

    /// <summary>
    /// Evicts oldest-first. Crude next to a true LRU, but tile access is dominated by the current
    /// viewport, so insertion order tracks usefulness closely enough and costs nothing to maintain.
    /// </summary>
    private void TrimResident()
    {
        while (_tiles.Count > MaxResidentTiles && _insertionOrder.TryDequeue(out var oldest))
        {
            if (_tiles.TryRemove(oldest, out var evicted))
                evicted?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _shutdown.Cancel();

        foreach (var image in _tiles.Values)
            image?.Dispose();

        _tiles.Clear();
        _shutdown.Dispose();
    }

    private readonly record struct TileKey(string Template, int Level, int X, int Y);
}
