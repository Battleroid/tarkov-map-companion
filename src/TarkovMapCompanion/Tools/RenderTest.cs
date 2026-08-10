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

        // Optional 5th arg: comma-separated floor names to switch on, e.g. "Tunnels".
        // Flags, position-independent:
        //   nobase  hide the ground level, the only way to see a floor underneath it
        //   bare    imagery only, no markers or heatmap, for comparing layers cleanly
        //   marks   lay a route of waypoints between the player and the exit
        //   quests  draw every positioned quest objective on this map
        var flags = args.Select(a => a.ToLowerInvariant()).ToHashSet();
        var includeBase = !flags.Contains("nobase");
        var bare = flags.Contains("bare");
        var marks = flags.Contains("marks");
        var quests = flags.Contains("quests");

        var floors = args.Length > 4 && args[4].Length > 0 && args[4] is not ("nobase" or "bare" or "marks" or "quests")
            ? args[4].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        var catalog = MapCatalog.LoadEmbedded();
        var map = catalog.Find(mapName);

        if (map is null)
        {
            Console.Error.WriteLine($"unknown map '{mapName}'. Available:");
            foreach (var candidate in catalog.Maps)
                Console.Error.WriteLine($"  {candidate.NormalizedName}");
            return 2;
        }

        if (map.Floors.Count > 0)
            Console.WriteLine($"floors     {string.Join(", ", map.Floors.Select(f => f.Name))}");

        foreach (var requested in floors)
        {
            if (!map.Floors.Any(f => f.Name.Equals(requested, StringComparison.OrdinalIgnoreCase)))
                Console.Error.WriteLine($"warning: '{requested}' is not a floor on this map");
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
        Console.WriteLine($"showing    ground={includeBase}, floors=[{string.Join(", ", floors)}]");

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
                source.Draw(warmup.Canvas, viewport, floors, includeBase);
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

            if (quests)
                overlays.Add(BuildQuestOverlay(map, store));

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

                var line = new ExtractLineOverlay
                {
                    Map = map,
                    Target = target,
                    PlayerPosition = lastFix.Position,
                    PlayerYawDegrees = lastFix.YawDegrees,
                };

                // A route laid out between the player and that exit, so the pins, the dashed
                // route through them, the arrival ring and the precedence over the exit are all
                // visible in one picture. The last pin is dropped right on the player to show a
                // reached marker as well.
                if (marks)
                {
                    var route = new WaypointOverlay { Map = map, ArrivalRadiusMeters = 50 };

                    // The reached marker goes first, on the player, so the route runs strictly away
                    // from them. It used to be added last, which sent the line out to the exit and
                    // straight back down its own length -- harmless when the route was a dash
                    // pattern, but now that the arrowheads carry the direction, an outbound and a
                    // return arrow land on top of each other and read as a row of asterisks.
                    route.Add(lastFix.Position, map.ToBase(lastFix.Position));

                    for (var step = 1; step <= 3; step++)
                    {
                        var t = step / 4.0;
                        var x = lastFix.Position.X + ((target.Position.X - lastFix.Position.X) * t);
                        var z = lastFix.Position.Z + ((target.Position.Z - lastFix.Position.Z) * t);

                        route.Add(new GamePosition(x, 0, z), map.Projection.ToBase(x, z));
                    }

                    route.ApplyFix(lastFix.Position);

                    // A teammate's route alongside your own, so the two can be told apart at a
                    // glance -- which is the whole question shared routes raise. Offset sideways
                    // from the player's line so they do not simply overlap.
                    var shared = new List<GamePosition>();

                    for (var step = 1; step <= 3; step++)
                    {
                        var t = step / 4.0;
                        var x = lastFix.Position.X + ((target.Position.X - lastFix.Position.X) * t);
                        var z = lastFix.Position.Z + ((target.Position.Z - lastFix.Position.Z) * t) + 90;

                        shared.Add(new GamePosition(x, 0, z));
                    }

                    route.SetSharedRoutes(
                    [
                        new WaypointOverlay.SharedRoute(
                            "Rudmere", map.NormalizedName, MarkerPalette.PeerColors[0], shared),
                    ]);

                    line.Waypoint = route.Next;
                    overlays.Add(route);

                    Console.WriteLine(
                        $"route      {route.Count} markers, next #{route.Next?.Number}, "
                        + $"{route.Waypoints.Count(w => w.Visited)} reached");
                }

                overlays.Add(line);

                Console.WriteLine(
                    $"guide      -> {(line.Waypoint is { } next ? $"marker #{next.Number}" : target.Name)}");
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

        // Bare mode keeps the imagery and drops everything drawn on top, which is what you want
        // when comparing two floor selections against each other.
        if (bare)
        {
            overlays.Clear();
            Console.WriteLine("overlays   none (bare)");
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

        source.Draw(canvas, viewport, floors, includeBase);
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
            source.Draw(canvas, viewport, floors, includeBase);
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
    /// <summary>
    /// Every positioned quest objective on a map, as if all of them were tracked.
    /// </summary>
    /// <remarks>
    /// Deliberately unlike the app, which only ever draws the tasks you ticked. Everything at once
    /// is the worst case for the marker geometry and the zone fills, which is what a render check
    /// is for.
    /// </remarks>
    private static QuestOverlay BuildQuestOverlay(GameMap map, MapDataStore store)
    {
        var tasks = new TaskStore(new Settings.AppSettings());
        tasks.LoadLocal();

        var marks = new List<QuestMark>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var catalog = MapCatalog.LoadEmbedded();

        foreach (var task in tasks.Tasks)
        {
            var forTask = new List<QuestMark>();

            foreach (var objective in task.Objectives)
            {
                foreach (var point in objective.Points)
                {
                    var resolved = catalog.ResolveByNameId(store.NameIdForId(point.MapId));
                    if (resolved is null || !string.Equals(resolved.NormalizedName, map.NormalizedName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!seen.Add($"{objective.Id}|{point.X}|{point.Z}|{point.OneOf}"))
                        continue;

                    forTask.Add(new QuestMark(
                        task.Id,
                        task.Name,
                        objective.Id,
                        objective.Description,
                        MarkerPalette.Quest,
                        new GamePosition(point.X, point.Y, point.Z),
                        Index: 0,
                        point.OneOf,
                        point.OutlinePoints.ToArray()));
                }
            }

            if (forTask.Count > 1)
            {
                for (var i = 0; i < forTask.Count; i++)
                    forTask[i] = forTask[i] with { Index = i + 1 };
            }

            marks.AddRange(forTask);
        }

        Console.WriteLine(
            $"quests     {marks.Count} objectives from "
            + $"{marks.Select(m => m.TaskId).Distinct().Count()} tasks, "
            + $"{marks.Count(m => m.Outline.Count > 2)} with a zone outline, "
            + $"{marks.Count(m => m.OneOf)} maybe-here");

        // Names off: with every task on the map at once the labels would be a solid wall of text,
        // and this render is about the marker and zone geometry.
        var overlay = new QuestOverlay { Map = map, ShowNames = false };
        overlay.SetMarks(marks);

        return overlay;
    }

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
