using TarkovMapCompanion.GameLog;
using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Tools;

/// <summary>
/// Prints where Tarkov's logs were found and what the parser makes of the newest one.
/// </summary>
/// <remarks>
/// The sibling of <c>--find-screenshots</c>, and for the same reason. "It is not switching maps" has
/// at least four causes -- the install was not found, the folder has no logs, the lines have changed
/// shape, or a location id is one this build does not know -- and they are indistinguishable from
/// the outside. This tells them apart in one pasted block.
/// </remarks>
public static class FindLogs
{
    /// <summary>How many recognized lines to show from the end of the newest log.</summary>
    private const int Recent = 20;

    public static int Run(string[] args)
    {
        var catalog = MapCatalog.LoadEmbedded();

        // "all" reads every launch's log rather than only the newest. Slower, and the right thing
        // when the question is "does this build understand every map I have played" rather than
        // "what is happening right now".
        var everything = args.Any(a => string.Equals(a, "all", StringComparison.OrdinalIgnoreCase));
        var folder = args.FirstOrDefault(a => !string.Equals(a, "all", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine("Looking for Escape from Tarkov logs...");
        Console.WriteLine();

        if (folder is not null)
        {
            Console.WriteLine($"Using the folder given on the command line: {folder}");
        }
        else
        {
            foreach (var candidate in GameLogFolders.Candidates())
            {
                var state = candidate switch
                {
                    { Looks: true } => $"{candidate.LogFolderCount} log folders",
                    { Exists: true } => "exists, but empty",
                    _ => "not there",
                };

                Console.WriteLine($"  [{state,-18}] {candidate.Path}");
                Console.WriteLine($"   {"",-20} via {candidate.Source}");
            }

            Console.WriteLine();
            folder = GameLogFolders.Detect();
        }

        if (folder is null)
        {
            Console.WriteLine("No Tarkov logs found in any of those.");
            Console.WriteLine();
            Console.WriteLine("Either the game has never been launched on this machine, or it is");
            Console.WriteLine("installed somewhere none of the above reaches. Find the folder holding");
            Console.WriteLine("EscapeFromTarkov.exe and point Settings at its Logs subfolder.");
            return 1;
        }

        Console.WriteLine($"Watching would use: {folder}");

        if (GameLogWatcher.NewestLog(folder) is not { } newest)
        {
            Console.WriteLine("...but there is no application log inside it yet.");
            return 1;
        }

        Console.WriteLine($"Newest log:         {newest}");
        Console.WriteLine($"Last written:       {File.GetLastWriteTime(newest):yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine();

        var files = everything
            ? Directory.EnumerateDirectories(folder)
                .SelectMany(GameLogFolders.ApplicationLogsIn)
                .OrderBy(f => File.GetLastWriteTimeUtc(f))
                .ToArray()
            : [newest];

        if (everything)
            Console.WriteLine($"Reading all {files.Length} logs.");

        var decoded = new List<GameLogEvent>();
        var lines = 0;

        foreach (var file in files)
        {
            // Streamed rather than read whole: a long session's log runs to hundreds of megabytes.
            using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line)
            {
                lines++;

                if (GameLogLineParser.Parse(line) is { } item)
                    decoded.Add(item);
            }
        }

        Console.WriteLine($"Read {lines} lines, recognized {decoded.Count}.");
        Console.WriteLine();

        if (decoded.Count == 0)
        {
            Console.WriteLine("Nothing in this log looked like a raid. If the game has been in a raid");
            Console.WriteLine("since it was written, the log format has changed and the parser needs");
            Console.WriteLine("updating; please open an issue with a few lines of the log.");
            return 1;
        }

        foreach (var item in decoded.TakeLast(Recent))
        {
            var when = item.At?.ToString("HH:mm:ss") ?? "--:--:--";
            var resolved = Describe(catalog, item);

            Console.WriteLine($"  {when}  {item.Kind,-12} {resolved}");
        }

        Console.WriteLine();

        var unresolved = decoded
            .Where(e => e.MapTokens.Count > 0 && catalog.ResolveByNameId(e.MapTokens[0]) is null
                                              && e.MapTokens.All(t => catalog.ResolveByNameId(t) is null))
            .SelectMany(e => e.MapTokens)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (unresolved.Length > 0)
        {
            Console.WriteLine("These names did not match any map this build knows about:");
            foreach (var token in unresolved)
                Console.WriteLine($"  {token}");

            Console.WriteLine();
            Console.WriteLine("That is the interesting part of this output. Please report it.");
            return 1;
        }

        Console.WriteLine("Every location name in this log resolved to a map.");
        return 0;
    }

    private static string Describe(MapCatalog catalog, GameLogEvent item)
    {
        if (item.MapTokens.Count == 0)
            return "";

        var map = item.MapTokens.Select(catalog.ResolveByNameId).FirstOrDefault(m => m is not null);
        var tokens = string.Join(" | ", item.MapTokens);
        var mode = item.RaidMode is null ? "" : $"  ({item.RaidMode})";

        return map is null
            ? $"{tokens} -> UNKNOWN{mode}"
            : $"{tokens} -> {map.NormalizedName}{mode}";
    }
}
