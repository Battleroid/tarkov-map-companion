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

            // Still reported: an unrecognized map name should not hide what the quest logs say.
            ReportQuests(folder);
            return 1;
        }

        Console.WriteLine("Every location name in this log resolved to a map.");

        ReportQuests(folder);
        return 0;
    }

    /// <summary>
    /// What the notification logs say about quests.
    /// </summary>
    /// <remarks>
    /// Here rather than in its own mode because the question is the same one: is the app reading
    /// this install correctly. An id that does not resolve is the interesting output, and it is the
    /// thing most likely to appear after a wipe.
    /// </remarks>
    private static void ReportQuests(string logsFolder)
    {
        using var watcher = new QuestLogWatcher();
        watcher.Start(logsFolder);
        watcher.Stop();

        var state = watcher.State;
        if (state.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("No quest notifications in these logs.");
            return;
        }

        var tasks = new Data.TaskStore(new Settings.AppSettings());
        tasks.LoadLocal();

        var known = tasks.Tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);

        ReportProfiles(watcher, known);

        var active = state.Where(p => p.Value == QuestProgress.Active).Select(p => p.Key).ToArray();
        var done = state.Count(p => p.Value == QuestProgress.Completed);
        var failed = state.Count(p => p.Value == QuestProgress.Failed);
        var unresolved = state.Keys.Where(id => !known.ContainsKey(id)).ToArray();

        Console.WriteLine();
        Console.WriteLine($"Quests: {active.Length} active, {done} completed, {failed} failed.");

        foreach (var id in active.OrderBy(id => known.TryGetValue(id, out var t) ? t.Name : id, StringComparer.OrdinalIgnoreCase))
        {
            if (!known.TryGetValue(id, out var task))
                continue;

            var placed = task.Objectives.Sum(o => o.Points.Count);
            Console.WriteLine($"  {task.Name,-44} {task.Trader,-12} {placed} positioned");
        }

        if (unresolved.Length == 0)
            return;

        Console.WriteLine();
        Console.WriteLine(
            $"{unresolved.Length} quest id(s) in the logs are not in the bundled data. Usually event or");
        Console.WriteLine("old-wipe quests; they are ignored rather than guessed at.");
    }

    /// <summary>
    /// One line per character the logs know about, and which one the app is answering for.
    /// </summary>
    /// <remarks>
    /// The first thing to look at when the app's idea of your level or your quests is somebody
    /// else's. One account has a PVE and a PVP character; they have separate quests and separate
    /// levels, and nothing in a trader message says which of them it was addressed to.
    /// </remarks>
    private static void ReportProfiles(
        QuestLogWatcher watcher,
        IReadOnlyDictionary<string, Data.Models.TaskData> known)
    {
        var profiles = watcher.ByProfile.Where(p => p.Value.Count > 0).ToArray();

        if (profiles.Length <= 1)
            return;

        Console.WriteLine();
        Console.WriteLine($"{profiles.Length} characters have quests in these logs:");

        foreach (var (id, quests) in profiles.OrderByDescending(p => p.Value.Count))
        {
            var active = quests.Count(q => q.Value == QuestProgress.Active);

            // The same floor the app applies: a quest with a level requirement cannot have been
            // accepted below it, so the highest one seen is a level this character is at least at.
            var floor = quests.Keys
                .Select(q => known.TryGetValue(q, out var t) ? t.MinPlayerLevel : 0)
                .DefaultIfEmpty(0)
                .Max();

            var mine = string.Equals(id, watcher.Profile, StringComparison.Ordinal) ? " <- following" : "";
            var name = id.Length == 0 ? "(not attributed)" : id;

            Console.WriteLine($"  {name}  {quests.Count,3} quests, {active,3} active, level {floor}+{mine}");
        }
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
