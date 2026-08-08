using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.Screenshots;

/// <summary>Why a candidate file was left alone. Surfaced in logs so refusals are never silent.</summary>
public enum CullRefusal
{
    None,
    Disabled,
    OutsideWatchedFolder,
    NotAScreenshotName,
    WithinKeepWindow,
    DeleteFailed,
}

public sealed record CullResult(string Path, bool Deleted, CullRefusal Refusal);

/// <summary>
/// Removes old screenshots from the watched folder.
/// </summary>
/// <remarks>
/// <para>
/// This is the only component that destroys anything the user owns, so it is built to refuse by
/// default. Three independent gates have to pass before a file is touched:
/// </para>
/// <list type="number">
///   <item><description>Culling is switched on. It is off out of the box.</description></item>
///   <item><description>The file sits directly inside the configured folder, verified on the
///     resolved full path so <c>..</c> and symlinked parents cannot walk out of it.</description></item>
///   <item><description>The name parses as a Tarkov screenshot. Anything else in that folder
///     belongs to someone else.</description></item>
/// </list>
/// <para>
/// Deletion goes to the Recycle Bin, and a failed shell call is reported rather than retried as a
/// hard delete.
/// </para>
/// </remarks>
public sealed class ScreenshotCuller(AppSettings settings)
{
    private readonly AppSettings _settings = settings;

    /// <summary>Set in tests to avoid touching the real Recycle Bin. Returns true on success.</summary>
    internal Func<string, bool>? DeleteOverride { get; set; }

    /// <summary>
    /// Applies the configured policy to <paramref name="folder"/>, which must be the folder the
    /// app is watching. Returns one result per file considered, deleted or not.
    /// </summary>
    public IReadOnlyList<CullResult> Apply(string folder, string? justReadFile = null)
    {
        if (_settings.CullMode == CullMode.Off)
            return [];

        if (!Directory.Exists(folder))
            return [];

        return _settings.CullMode switch
        {
            CullMode.DeleteAfterRead => justReadFile is null ? [] : [Delete(folder, justReadFile)],
            CullMode.KeepLatest => ApplyKeepLatest(folder),
            _ => [],
        };
    }

    private IReadOnlyList<CullResult> ApplyKeepLatest(string folder)
    {
        var screenshots = EnumerateScreenshots(folder)
            // Newest first. The name carries the capture time, but sorting on it would misorder
            // files whose names are malformed in a way the regex still accepts, so use the name
            // itself as the tiebreak -- the game's format sorts chronologically as text.
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (screenshots.Length <= _settings.CullKeepCount)
            return [];

        var results = new List<CullResult>();

        foreach (var path in screenshots.Skip(_settings.CullKeepCount))
            results.Add(Delete(folder, path));

        return results;
    }

    /// <summary>Screenshot files directly inside <paramref name="folder"/>, never recursively.</summary>
    /// <remarks>
    /// Materialized rather than returned lazily. <see cref="Directory.EnumerateFiles(string)"/>
    /// does its work as the caller iterates, so a try/catch around the call itself catches
    /// nothing: the failure surfaces later, in whichever thread happens to be enumerating. That
    /// thread is the reconcile timer, where an escaping exception ends the process. Enumerating
    /// here means the catch actually covers the IO.
    /// </remarks>
    public static IReadOnlyList<string> EnumerateScreenshots(string folder)
    {
        try
        {
            return Directory
                .EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(ScreenshotNameParser.IsScreenshotFileName)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // The folder can vanish or be locked while the game is writing into it.
            Diagnostics.Log.Warn($"could not list {folder}: {ex.Message}");
            return [];
        }
    }

    private CullResult Delete(string folder, string path)
    {
        if (!IsInsideFolder(folder, path))
            return new CullResult(path, false, CullRefusal.OutsideWatchedFolder);

        if (!ScreenshotNameParser.IsScreenshotFileName(path))
            return new CullResult(path, false, CullRefusal.NotAScreenshotName);

        if (!File.Exists(path))
            return new CullResult(path, false, CullRefusal.DeleteFailed);

        var deleter = DeleteOverride ?? DefaultDelete;

        bool deleted;
        try
        {
            deleted = deleter(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            deleted = false;
        }

        return deleted
            ? new CullResult(path, true, CullRefusal.None)
            : new CullResult(path, false, CullRefusal.DeleteFailed);
    }

    private bool DefaultDelete(string path)
    {
        if (_settings.CullToRecycleBin)
            return RecycleBin.TryDelete(path);

        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a direct child of <paramref name="folder"/>, compared on
    /// fully resolved paths so relative segments cannot escape the watched directory.
    /// </summary>
    internal static bool IsInsideFolder(string folder, string path)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(path))
            return false;

        string resolvedFolder, resolvedParent;
        try
        {
            resolvedFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            if (parent is null)
                return false;

            resolvedParent = Path.TrimEndingDirectorySeparator(parent);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(resolvedFolder, resolvedParent, comparison);
    }
}
