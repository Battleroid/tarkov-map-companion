using Microsoft.VisualBasic.FileIO;

namespace TarkovMapCompanion.Screenshots;

/// <summary>
/// Moves files to the Windows Recycle Bin instead of unlinking them.
/// </summary>
/// <remarks>
/// <para>
/// Culling exists to keep a folder tidy, not to destroy anything. Recoverable deletion is the
/// difference between a tidy-up and data loss when a filter turns out to be wrong.
/// </para>
/// <para>
/// This used to hand-marshal <c>SHFileOperationW</c>. That struct was declared with
/// <c>Pack = 1</c> while the native <c>SHFILEOPSTRUCTW</c> uses natural alignment, so every field
/// after <c>wFunc</c> sat at the wrong offset, the shell read the source path from the wrong place
/// and dereferenced garbage. The result was an AccessViolationException on the folder-watcher
/// thread, which .NET does not deliver to catch blocks -- so the app vanished the moment a
/// screenshot triggered a cull, with nothing logged.
/// </para>
/// <para>
/// The runtime already ships a correct, tested implementation of exactly this. Using it removes
/// the marshalling from our hands entirely, which is worth more here than avoiding the dependency.
/// </para>
/// </remarks>
public static class RecycleBin
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Sends a single file to the Recycle Bin. Returns false if it could not be deleted, leaving
    /// the file untouched; callers must not fall back to a hard delete on failure.
    /// </summary>
    public static bool TryDelete(string path)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            // OnlyErrorDialogs: no progress or confirmation UI, but a genuine failure still
            // surfaces rather than being swallowed.
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or OperationCanceledException
                                       or ArgumentException
                                       or NotSupportedException
                                       or FileNotFoundException)
        {
            Diagnostics.Log.Warn($"could not recycle {path}: {ex.Message}");
            return false;
        }
    }
}
