using System.Diagnostics;
using SkiaSharp;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Screenshots;

namespace TarkovMapCompanion.Tools;

/// <summary>
/// Renders a map straight to a PNG, with no window involved.
/// </summary>
/// <remarks>
/// This is the project's main visual check. Coordinate bugs are close to invisible in unit tests --
/// a marker 20 meters out still passes every numeric assertion you thought to write -- but they are
/// obvious the moment extract markers sit off the extract shapes drawn into the map artwork.
/// Being headless, it also runs over SSH, in CI, and under a coding agent.
/// </remarks>
public static class RenderTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        var mapName = args.Length > 0 ? args[0] : "shoreline";
        var outputPath = args.Length > 1 ? args[1] : $"{mapName}.render.png";
        var width = args.Length > 2 && int.TryParse(args[2], out var w) ? w : 1600;
        var height = args.Length > 3 && int.TryParse(args[3], out var h) ? h : 1100;

        var catalog = MapCatalog.LoadEmbedded();
        var map = catalog.Find(mapName);

        if (map is null)
        {
            Console.Error.WriteLine($"unknown map '{mapName}'. Available:");
            foreach (var candidate in catalog.Maps)
                Console.Error.WriteLine($"  {candidate.NormalizedName}");
            return 2;
        }

        Console.WriteLine($"map        {map.DisplayName} ({map.NormalizedName})");
        Console.WriteLine($"rotation   {map.Projection.CoordinateRotationDegrees} deg");
        Console.WriteLine($"base rect  {map.BaseRect.Width:F1} x {map.BaseRect.Height:F1}");
        Console.WriteLine($"imagery    svg={map.HasSvg} tiles={map.HasTiles}");

        var assets = new AssetCache();

        // Same choice the app makes: vector where it exists, tiles for the three maps without it.
        using IMapImageSource source = map.HasSvg
            ? new SvgMapSource(map, assets)
            : new TileMapSource(map, assets);

        Console.WriteLine($"renderer   {source.Name}");

        var stopwatch = Stopwatch.StartNew();
        await source.LoadAsync().ConfigureAwait(false);
        Console.WriteLine($"load       {stopwatch.ElapsedMilliseconds} ms");

        if (!source.IsReady)
        {
            Console.Error.WriteLine("map imagery could not be fetched and is not cached.");
            return 4;
        }

        var viewport = new Viewport(map.BaseRect);
        viewport.Resize(width, height);
        viewport.FitAll();

        // Tiles stream in on background requests, so the first draw only queues them. Draw
        // repeatedly until the picture stops changing before taking the snapshot.
        if (source is TileMapSource)
        {
            using var warmup = SKSurface.Create(new SKImageInfo(width, height));
            for (var attempt = 0; attempt < 24 && warmup is not null; attempt++)
            {
                source.Draw(warmup.Canvas, viewport, []);
                await Task.Delay(200).ConfigureAwait(false);
            }
            Console.WriteLine("tiles      warmed");
        }

        var overlays = new List<IMapOverlay>();

        // Extract markers on top of the extract zones the artwork already draws. This is the
        // strongest check available: the two come from different sources, so if the diamonds land
        // on the red hatched strips, the projection and the POI pipeline are both right.
        var settings = new Settings.AppSettings();
        var store = new MapDataStore(settings);
        store.LoadLocal();
        Console.WriteLine($"poi data   {store.Origin}");

        if (store.ForMap(map.NormalizedName) is { } poiData)
        {
            var pois = PoiBuilder.Build(map, poiData, store);
            var byKind = pois.GroupBy(p => p.Kind).ToDictionary(g => g.Key, g => g.Count());

            Console.WriteLine($"pois       {pois.Count} total: " +
                string.Join(", ", byKind.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
            Console.WriteLine($"raid       {poiData.RaidDuration} min");

            var heatmap = new HeatmapOverlay { Map = map, IsVisible = true, Opacity = 0.6 };
            heatmap.SetData(poiData);
            heatmap.Groups[SpawnGroup.Pmc] = true;
            heatmap.Groups[SpawnGroup.Scav] = true;
            Console.WriteLine($"heatmap    pmc={heatmap.PointCount(SpawnGroup.Pmc)} " +
                              $"scav={heatmap.PointCount(SpawnGroup.Scav)} " +
                              $"aipmc={heatmap.PointCount(SpawnGroup.AiPmc)} " +
                              $"boss={heatmap.PointCount(SpawnGroup.Boss)}");
            overlays.Add(heatmap);

            var overlay = new PoiOverlay { Map = map };
            overlay.SetPois(pois);

            // Everything except the dense loot layer, which would bury the map.
            foreach (var kind in Enum.GetValues<PoiKind>())
                overlay.Visible[kind] = kind != PoiKind.LootContainer;

            overlays.Add(overlay);

            // Guide line to the furthest PMC exit from the last known position, which exercises
            // the distance and relative-bearing readout at a length that is easy to eyeball.
            var lastFix = LoadRealFixes(map).LastOrDefault();
            var target = lastFix is null
                ? null
                : pois.Where(p => p.Kind == PoiKind.ExtractPmc)
                      .OrderByDescending(p => lastFix.Position.GroundDistanceTo(p.Position))
                      .FirstOrDefault();

            if (lastFix is not null && target is not null)
            {
                overlay.Selected = target;

                overlays.Add(new ExtractLineOverlay
                {
                    Map = map,
                    Target = target,
                    PlayerPosition = lastFix.Position,
                    PlayerYawDegrees = lastFix.YawDegrees,
                });

                Console.WriteLine($"guide      -> {target.Name}");
            }
        }
        else
        {
            Console.WriteLine("poi data   none for this map");
        }

        // Landmark names from the map catalog, an independent check on the affine.
        if (map.Labels.Count > 0)
            overlays.Add(new DebugLabelOverlay(map));

        // Plot the real screenshots when they are available, so the render shows a genuine path
        // through the map rather than synthetic points.
        var fixes = LoadRealFixes(map);
        if (fixes.Count > 0)
        {
            Console.WriteLine($"fixes      {fixes.Count} real screenshot positions on this map");
            overlays.Add(new DebugTrackOverlay(fixes, map));
        }
        else
        {
            Console.WriteLine("fixes      none for this map; drawing a bounds probe instead");
            overlays.Add(new DebugBoundsOverlay(map));
        }

        stopwatch.Restart();

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface is null)
        {
            Console.Error.WriteLine("could not create a Skia surface");
            return 5;
        }

        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x12, 0x15, 0x1A));

        source.Draw(canvas, viewport, []);
        foreach (var overlay in overlays)
            overlay.Draw(canvas, viewport);

        canvas.Flush();
        Console.WriteLine($"render     {stopwatch.ElapsedMilliseconds} ms (first frame: parse + rasterize)");

        // Steady state is what the app actually lives in: the snapshot is warm and each frame is
        // a blit plus overlays. If this is not comfortably inside a 16 ms budget, panning stutters.
        stopwatch.Restart();
        const int frames = 30;
        for (var i = 0; i < frames; i++)
        {
            source.Draw(canvas, viewport, []);
            foreach (var overlay in overlays)
                overlay.Draw(canvas, viewport);
        }
        canvas.Flush();
        Console.WriteLine($"redraw     {stopwatch.Elapsed.TotalMilliseconds / frames:F2} ms/frame over {frames} frames");

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        await using (var file = File.Create(outputPath))
            data.SaveTo(file);

        Console.WriteLine($"wrote      {Path.GetFullPath(outputPath)} ({data.Size / 1024} KB)");
        return 0;
    }

    private static List<PlayerFix> LoadRealFixes(GameMap map)
    {
        var folder = Settings.AppSettings.DefaultScreenshotFolder();
        if (!Directory.Exists(folder))
            return [];

        return ScreenshotWatcher.ReadFolder(folder)
            .Where(fix => map.ContainsPosition(fix.Position))
            .ToList();
    }

    /// <summary>Draws the recorded path, with a heading arrow at each fix.</summary>
    private sealed class DebugTrackOverlay(IReadOnlyList<PlayerFix> fixes, GameMap map) : IMapOverlay
    {
        public int ZOrder => 100;
        public bool IsVisible => true;

        public void Draw(SKCanvas canvas, Viewport viewport)
        {
            using var line = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                Color = new SKColor(0xD8, 0xA6, 0x57, 0xC0),
                IsAntialias = true,
            };

            using var path = new SKPath();
            for (var i = 0; i < fixes.Count; i++)
            {
                var screen = viewport.ToScreen(map.ToBase(fixes[i].Position));
                if (i == 0)
                    path.MoveTo((float)screen.X, (float)screen.Y);
                else
                    path.LineTo((float)screen.X, (float)screen.Y);
            }
            canvas.DrawPath(path, line);

            using var fill = new SKPaint { Color = new SKColor(0xD8, 0xA6, 0x57), IsAntialias = true };
            using var outline = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f,
                Color = SKColors.Black,
                IsAntialias = true,
            };

            foreach (var fix in fixes)
            {
                var screen = viewport.ToScreen(map.ToBase(fix.Position));
                var angle = map.Projection.ScreenAngleDegrees(fix.YawDegrees);

                canvas.Save();
                canvas.Translate((float)screen.X, (float)screen.Y);
                canvas.RotateDegrees((float)angle);

                // A blunt arrowhead pointing up before rotation.
                using var arrow = new SKPath();
                arrow.MoveTo(0, -9);
                arrow.LineTo(6, 7);
                arrow.LineTo(0, 3.5f);
                arrow.LineTo(-6, 7);
                arrow.Close();

                canvas.DrawPath(arrow, fill);
                canvas.DrawPath(arrow, outline);
                canvas.Restore();
            }
        }
    }

    /// <summary>
    /// Plots each landmark name at its game position. Any rotation, mirror or offset error in the
    /// projection shows up immediately as names sitting on the wrong buildings.
    /// </summary>
    private sealed class DebugLabelOverlay(GameMap map) : IMapOverlay
    {
        public int ZOrder => 90;
        public bool IsVisible => true;

        public void Draw(SKCanvas canvas, Viewport viewport)
        {
            var typeface = SKTypeface.FromFamilyName("Consolas") ?? SKTypeface.Default;

            using var text = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true,
                Typeface = typeface,
                TextSize = 13,
            };
            using var halo = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 220),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3,
                Typeface = typeface,
                TextSize = 13,
            };
            using var dot = new SKPaint { Color = new SKColor(0x4F, 0xC3, 0xF7), IsAntialias = true };

            foreach (var label in map.Labels)
            {
                var position = label.Position!;
                var screen = viewport.ToScreen(map.Projection.ToBase(position[0], position[1]));

                canvas.DrawCircle((float)screen.X, (float)screen.Y, 3.5f, dot);

                var x = (float)screen.X + 6;
                var y = (float)screen.Y - 5;
                canvas.DrawText(label.Text, x, y, halo);
                canvas.DrawText(label.Text, x, y, text);
            }
        }
    }

    /// <summary>Marks the map's corners and center, to confirm the imagery lands on its bounds.</summary>
    private sealed class DebugBoundsOverlay(GameMap map) : IMapOverlay
    {
        public int ZOrder => 100;
        public bool IsVisible => true;

        public void Draw(SKCanvas canvas, Viewport viewport)
        {
            using var stroke = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                Color = new SKColor(0xE0, 0x6C, 0x75),
                IsAntialias = true,
            };

            var rect = viewport.ToScreen(map.BaseRect);
            canvas.DrawRect(
                new SKRect((float)rect.Left, (float)rect.Top, (float)rect.Right, (float)rect.Bottom),
                stroke);

            var center = viewport.ToScreen(map.BaseRect.Center);
            canvas.DrawCircle((float)center.X, (float)center.Y, 6, stroke);
        }
    }
}
