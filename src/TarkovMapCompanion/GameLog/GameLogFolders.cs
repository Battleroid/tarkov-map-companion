using Microsoft.Win32;

namespace TarkovMapCompanion.GameLog;

/// <summary>A place Tarkov's logs might be, and what was actually found there.</summary>
/// <param name="Path">The <c>Logs</c> directory itself, not the install root.</param>
/// <param name="LogFolderCount">How many per-launch folders in it hold an application log.</param>
public sealed record GameLogFolder(string Path, string Source, bool Exists, int LogFolderCount)
{
    /// <summary>Logs actually present is the only evidence that really settles it.</summary>
    public bool Looks => Exists && LogFolderCount > 0;
}

/// <summary>
/// Works out where Escape from Tarkov is installed, by finding where it writes its logs.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the same shape as <c>ScreenshotFolders</c>, for the same reason: reasoning about
/// where an installer "should" have put things is how you end up watching an empty directory
/// forever. Look in every plausible place, prefer whichever one has logs sitting in it.
/// </para>
/// <para>
/// Tarkov is worse than most for this. The launcher lets you install anywhere, the registry is
/// frequently absent entirely, and the logs live under the install directory rather than in
/// AppData. On the development machine the game is at <c>A:\Other\Tarkov</c>, which no amount of
/// guessing at Program Files would ever reach.
/// </para>
/// <para>
/// What does work, on any machine that has launched the game once, is the game's own Unity log: it
/// records its data directory in the first few lines and lives at a fixed path. Everything below it
/// in the list is a fallback for the case where that log has been cleared.
/// </para>
/// </remarks>
public static class GameLogFolders
{
    /// <summary>The subdirectory of the install that holds per-launch log folders.</summary>
    public const string LogsFolderName = "Logs";

    /// <summary>The directory Unity writes beside the executable. Anchors every path scan.</summary>
    private const string DataFolderMarker = "EscapeFromTarkov_Data";

    /// <summary>How much of a log to read while looking for a path. The line is near the top.</summary>
    private const int ScanBytes = 512 * 1024;

    /// <summary>Every place worth looking, best evidence first.</summary>
    public static IReadOnlyList<GameLogFolder> Candidates() => Evaluate(Roots());

    /// <summary>Install roots to consider, in the order they were thought of.</summary>
    private static IEnumerable<(string? Root, string Source)> Roots()
    {
        foreach (var root in FromUnityLogs())
            yield return (root, "the game's own Unity log");

        foreach (var root in FromLauncherLogs())
            yield return (root, "launcher log");

        foreach (var root in FromRegistry())
            yield return (root, "registry");

        foreach (var root in CommonInstallRoots())
            yield return (root, "common location");
    }

    /// <summary>
    /// Turns install roots into log folders and weighs them. Split out so tests can drive it with
    /// synthetic roots rather than whatever this machine happens to have installed.
    /// </summary>
    internal static IReadOnlyList<GameLogFolder> Evaluate(IEnumerable<(string? Root, string Source)> roots)
    {
        var found = new List<GameLogFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, source) in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            string path;
            try
            {
                path = Path.GetFullPath(Path.Combine(root, LogsFolderName));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!seen.Add(path))
                continue;

            var exists = Directory.Exists(path);
            found.Add(new GameLogFolder(path, source, exists, exists ? CountLogFolders(path) : 0));
        }

        return found
            .OrderByDescending(f => f.Looks)
            .ThenByDescending(f => f.LogFolderCount)
            .ThenByDescending(f => f.Exists)
            .ToArray();
    }

    /// <summary>The best guess at the folder to watch, or null when nothing was found.</summary>
    /// <remarks>
    /// Null rather than a canonical fallback, unlike the screenshot equivalent. There is no
    /// canonical path to fall back to, and pretending there is would turn "the game is not
    /// installed where I can see it" into a watcher pointed at a directory that will never exist.
    /// </remarks>
    public static string? Detect()
    {
        var candidates = Candidates();

        return candidates.FirstOrDefault(c => c.Looks)?.Path
               ?? candidates.FirstOrDefault(c => c.Exists)?.Path;
    }

    /// <summary>How many per-launch folders with an application log are in a Logs directory.</summary>
    public static int CountLogFolders(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return 0;

        try
        {
            return Directory.EnumerateDirectories(folder)
                .Count(d => ApplicationLogsIn(d).Count > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The application logs inside one per-launch folder, newest suffix last.
    /// </summary>
    /// <remarks>
    /// The name is the launch folder's own name with <c> application_000.log</c> appended, and the
    /// suffix rolls to <c>_001</c> in a long session, so the pattern is loose and the ordering
    /// does the work.
    /// </remarks>
    public static IReadOnlyList<string> ApplicationLogsIn(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*application*.log", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // ---- Sources ------------------------------------------------------------

    /// <summary>
    /// The install path out of the game's own Unity log.
    /// </summary>
    /// <remarks>
    /// Unity writes <c>Discovering subsystems at path &lt;install&gt;/EscapeFromTarkov_Data/...</c>
    /// within the first few lines of every launch, at a path that does not move. It is the only
    /// automatic source that worked on the development machine, where the game is on a second drive
    /// and the registry has nothing.
    /// </remarks>
    private static IEnumerable<string> FromUnityLogs()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Battlestate Games", "EscapeFromTarkov");

        foreach (var name in (string[])["Player.log", "Player-prev.log"])
        {
            foreach (var root in InstallRootsIn(ReadHead(Path.Combine(folder, name))))
                yield return root;
        }
    }

    /// <summary>
    /// The install path out of the launcher's log, which records every file it patches.
    /// </summary>
    /// <remarks>
    /// A second opinion for the case where the Unity log has been cleared. Newest first, and only a
    /// couple of files: these run to megabytes and the whole point is to be quick about it.
    /// </remarks>
    private static IEnumerable<string> FromLauncherLogs()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Battlestate Games", "BsgLauncher", "Logs");

        if (!Directory.Exists(folder))
            yield break;

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(folder, "BSG_Launcher_*.log")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            foreach (var root in InstallRootsIn(ReadHead(file, 4 * 1024 * 1024)))
                yield return root;
        }
    }

    private static IEnumerable<string> FromRegistry()
    {
        // Absent on the development machine, present on plenty of others, and cheap either way.
        foreach (var (hive, subKey) in ((RegistryKey, string)[])
                 [
                     (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkov"),
                     (Registry.CurrentUser, @"SOFTWARE\Battlestate Games\EscapeFromTarkov"),
                 ])
        {
            string? value = null;

            try
            {
                using var key = hive.OpenSubKey(subKey);
                value = key?.GetValue("InstallLocation") as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // A locked-down machine is not a reason to stop looking in the other places.
            }

            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
    }

    private static IEnumerable<string> CommonInstallRoots()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Battlestate Games", "EFT");

        string[] tails = ["Battlestate Games\\EFT", "Games\\EFT", "EFT", "Tarkov", "Escape from Tarkov"];

        foreach (var drive in FixedDrives())
        {
            foreach (var tail in tails)
                yield return Path.Combine(drive, tail);
        }
    }

    private static IEnumerable<string> FixedDrives()
    {
        DriveInfo[] drives;

        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return drives
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .ToArray();
    }

    // ---- Path extraction ----------------------------------------------------

    /// <summary>
    /// Every install root mentioned in a blob of log text.
    /// </summary>
    /// <remarks>
    /// Anchored on the data folder and walked <em>backwards</em> to the drive colon rather than
    /// matched forwards with a regex. That is what makes it survive the launcher's habit of putting
    /// two paths on one line: given
    /// <c>A:\...\eft_live.bsgp to directory A:\Other\Tarkov\EscapeFromTarkov_Data</c>, a
    /// forward match starting at the first drive letter swallows the prose in the middle, while the
    /// last colon before the marker is the right one every time. It also means a path containing
    /// spaces, which Program Files does, still comes out whole.
    /// </remarks>
    internal static IEnumerable<string> InstallRootsIn(string? text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var search = 0;

        while (true)
        {
            var marker = text.IndexOf(DataFolderMarker, search, StringComparison.OrdinalIgnoreCase);
            if (marker < 2)
                yield break;

            search = marker + DataFolderMarker.Length;

            // The separator immediately before the marker, then the drive colon before that.
            var separator = marker - 1;
            if (text[separator] is not ('\\' or '/'))
                continue;

            var colon = text.LastIndexOf(':', separator);
            if (colon < 1 || colon + 1 >= text.Length || text[colon + 1] is not ('\\' or '/'))
                continue;

            if (!char.IsLetter(text[colon - 1]))
                continue;

            var root = text[(colon - 1)..separator];

            // Not checked against the disk here. Whether the directory is really there is decided
            // in one place, by Evaluate looking for logs in it, and keeping this pure is what lets
            // it be tested against captured log text.
            if (root.Length > 3 && seen.Add(root))
                yield return root;
        }
    }

    /// <summary>Reads the start of a file, tolerating the game holding it open.</summary>
    private static string ReadHead(string path, int limit = ScanBytes)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var length = (int)Math.Min(limit, stream.Length);
            var buffer = new byte[length];
            stream.ReadExactly(buffer);

            return System.Text.Encoding.UTF8.GetString(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "";
        }
    }
}
