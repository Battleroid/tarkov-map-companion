using System.Diagnostics;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Settings;
using TarkovMapCompanion.Vision;

namespace TarkovMapCompanion.Tools;

/// <summary>
/// Reads the extraction panel out of one screenshot and prints every stage of the decision.
/// </summary>
/// <remarks>
/// The feature is only as good as the read, and when it gets something wrong the useful question is
/// which stage went wrong: did the reader mangle the text, did the row grouping split a row, or did
/// the name simply not match anything on this map? Printing all three makes that a ten-second
/// answer instead of a guess, for whoever hits a bad read on a map or resolution I never saw.
/// </remarks>
public static class ReadExits
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: --read-exits <screenshot.png> [map] [whole]");
            return 2;
        }

        var path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"no such file: {path}");
            return 2;
        }

        var mapName = args.Length > 1 && args[1] is not "whole" ? args[1] : "customs";
        var region = args.Contains("whole") ? RelativeRegion.Whole : RelativeRegion.ExtractPanel;

        var catalog = MapCatalog.LoadEmbedded();
        var map = catalog.Find(mapName);
        if (map is null)
        {
            Console.Error.WriteLine($"unknown map '{mapName}'");
            return 2;
        }

        var reader = new WindowsOcrTextReader();
        Console.WriteLine($"reader     available={reader.IsAvailable} {reader.UnavailableReason}");
        if (!reader.IsAvailable)
            return 3;

        Console.WriteLine($"file       {Path.GetFileName(path)}");
        Console.WriteLine($"map        {map.DisplayName}");
        Console.WriteLine($"region     {region}");

        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        var lines = await reader.ReadAsync(bytes, region).ConfigureAwait(false);
        stopwatch.Stop();

        Console.WriteLine($"read       {lines.Count} lines in {stopwatch.ElapsedMilliseconds} ms");
        Console.WriteLine();

        Console.WriteLine("-- text ------------------------------------------------------------");
        foreach (var line in lines.OrderBy(l => l.Bounds.CenterY).ThenBy(l => l.Bounds.X))
            Console.WriteLine($"  [x{line.Bounds.X,6:F0} y{line.Bounds.Y,6:F0}]  {line.Text}");

        var reading = ExtractPanelParser.Parse(lines);

        Console.WriteLine();
        Console.WriteLine("-- rows ------------------------------------------------------------");
        Console.WriteLine($"  panel found: {reading.PanelFound}");
        foreach (var row in reading.Rows)
            Console.WriteLine($"  {row.Kind,-12} '{row.Name}'   (raw: {row.RawText})");

        if (!reading.PanelFound)
        {
            Console.WriteLine();
            Console.WriteLine("No extraction panel in this screenshot; exits would be left alone.");
            return 0;
        }

        // Same POI set the app matches against.
        var settings = new AppSettings();
        var store = new MapDataStore(settings);
        store.LoadLocal();

        var data = store.ForMap(map.NormalizedName);
        if (data is null)
        {
            Console.Error.WriteLine("no POI data for this map in the embedded snapshot");
            return 4;
        }

        var notes = new ExtractNotesStore();
        notes.Load();

        var exits = PoiBuilder.Build(map, data, store, notes)
            .Where(p => p.IsExtract || p.Kind == PoiKind.Transit)
            .ToArray();

        var availability = ExitAvailability.Resolve(reading, exits, map.NormalizedName, DateTime.Now);

        Console.WriteLine();
        Console.WriteLine("-- resolution ------------------------------------------------------");

        if (availability is null)
        {
            Console.WriteLine("  nothing resolved; exits would be left alone");
            return 0;
        }

        foreach (var poi in exits.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var included = availability.Includes(poi);
            Console.WriteLine($"  {(included ? "AVAILABLE" : "  dimmed ")}  [{poi.FactionLabel,-12}] {poi.Name}");
        }

        Console.WriteLine();
        Console.WriteLine($"  {exits.Count(availability.Includes)} of {exits.Length} exits available");

        foreach (var unresolved in availability.Unresolved)
            Console.WriteLine($"  UNRESOLVED: '{unresolved}'");

        return 0;
    }
}
