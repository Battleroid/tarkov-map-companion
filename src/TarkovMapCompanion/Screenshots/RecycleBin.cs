using Windows.Storage;

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
/// The replacement for that was VisualBasic's FileSystem.DeleteFile with UIOption.OnlyErrorDialogs,
/// which fixed the crash but introduced a quieter fault: "only error dialogs" still means dialogs.
/// A failed delete raised a modal Windows error box -- "The parameter is incorrect" and friends --
/// from the folder-watcher thread, which blocks that thread on a window the user never asked for
/// and, from the outside, is indistinguishable from the app having thrown an error at them
/// mid-raid. The stray "the operation was canceled" warnings in the logs were those dialogs being
/// dismissed.
/// </para>
/// <para>
/// WinRT's StorageFile.DeleteAsync with StorageDeleteOption.Default recycles the file and shows no
/// UI at all, which is what was wanted from the start. No struct marshalling, and no windows.
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
            // StorageDeleteOption.Default is the Recycle Bin, and this shows no UI of any kind.
            var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            file.DeleteAsync(StorageDeleteOption.Default).AsTask().GetAwaiter().GetResult();

            return true;
        }
        catch (Exception ex)
        {
            // Deliberately broad. This runs on the folder-watcher thread, where an escaping
            // exception ends the process, and the shell can surface almost anything through here.
            Diagnostics.Log.Warn($"could not recycle {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
