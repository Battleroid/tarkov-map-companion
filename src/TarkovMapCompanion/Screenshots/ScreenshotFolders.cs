namespace TarkovMapCompanion.Screenshots;

/// <summary>A place Tarkov screenshots might be, and what was actually found there.</summary>
public sealed record ScreenshotFolder(string Path, string Source, bool Exists, int ScreenshotCount)
{
    /// <summary>Screenshots present is the only evidence that really settles it.</summary>
    public bool Looks => Exists && ScreenshotCount > 0;
}

/// <summary>
/// Works out where Escape from Tarkov is actually writing screenshots.
/// </summary>
/// <remarks>
/// <para>
/// Assuming <c>Documents\Escape from Tarkov\Screenshots</c> is right most of the time and wrong in
/// a way nobody can diagnose. OneDrive's Known Folder Move relocates Documents, and whether the
/// shell reports the moved path or the original depends on how completely the move was applied.
/// The failure is silent: the app watches a folder, nothing ever appears in it, and the user is
/// left with a map that simply never moves.
/// </para>
/// <para>
/// So rather than one guess, look in all the plausible places and prefer whichever one has Tarkov
/// screenshots sitting in it. Files on disk beat any amount of reasoning about shell folders.
/// </para>
/// </remarks>
public static class ScreenshotFolders
{
    private const string Suffix = @"Escape from Tarkov\Screenshots";

    /// <summary>
    /// Every place worth looking, best evidence first.
    /// </summary>
    public static IReadOnlyList<ScreenshotFolder> Candidates() => Evaluate(Roots());

    /// <summary>The places to look, in the order they were thought of. Split out to be testable.</summary>
    private static IEnumerable<(string? Root, string Source)> Roots()
    {
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "shell Documents");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return (System.IO.Path.Combine(profile, "Documents"), "profile Documents");

        yield return (Environment.GetEnvironmentVariable("OneDrive") is { } d
            ? System.IO.Path.Combine(d, "Documents") : null, "OneDrive");

        yield return (Environment.GetEnvironmentVariable("OneDriveCommercial") is { } c
            ? System.IO.Path.Combine(c, "Documents") : null, "OneDrive for Business");

        // Catches tenant-named folders like "OneDrive - Contoso" that neither variable points at.
        foreach (var directory in SafeDirectories(profile, "OneDrive*"))
            yield return (System.IO.Path.Combine(directory, "Documents"), "OneDrive folder");
    }

    internal static IReadOnlyList<ScreenshotFolder> Evaluate(IEnumerable<(string? Root, string Source)> roots)
    {
        var found = new List<ScreenshotFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? root, string source)
        {
            if (string.IsNullOrWhiteSpace(root))
                return;

            string path;
            try
            {
                path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, Suffix));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            if (!seen.Add(path))
                return;

            var exists = Directory.Exists(path);
            var count = exists ? ScreenshotCuller.EnumerateScreenshots(path).Count : 0;

            found.Add(new ScreenshotFolder(path, source, exists, count));
        }

        foreach (var (root, source) in roots)
            Consider(root, source);

        // Screenshots present wins; then merely existing; then the order above.
        return found
            .OrderByDescending(f => f.Looks)
            .ThenByDescending(f => f.ScreenshotCount)
            .ThenByDescending(f => f.Exists)
            .ToArray();
    }

    /// <summary>
    /// The best guess at the folder to watch.
    /// </summary>
    /// <remarks>
    /// Falls back to the canonical path when nothing is found, so a first run before the game has
    /// ever been launched still points somewhere sensible rather than nowhere.
    /// </remarks>
    public static string Detect()
    {
        var candidates = Candidates();

        return candidates.FirstOrDefault(c => c.Looks)?.Path
               ?? candidates.FirstOrDefault(c => c.Exists)?.Path
               ?? candidates.FirstOrDefault()?.Path
               ?? System.IO.Path.Combine(
                   Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Suffix);
    }

    /// <summary>How many Tarkov screenshots are in a folder right now, for showing the user.</summary>
    public static int CountIn(string? folder) =>
        string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)
            ? 0
            : ScreenshotCuller.EnumerateScreenshots(folder).Count;

    private static IEnumerable<string> SafeDirectories(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateDirectories(root, pattern, SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
