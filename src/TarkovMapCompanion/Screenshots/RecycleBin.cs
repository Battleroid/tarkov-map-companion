using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TarkovMapCompanion.Screenshots;

/// <summary>
/// Moves files to the Windows Recycle Bin instead of unlinking them.
/// </summary>
/// <remarks>
/// Culling exists to keep a folder tidy, not to destroy anything. Recoverable deletion is the
/// difference between a tidy-up and data loss when a filter turns out to be wrong.
/// </remarks>
public static class RecycleBin
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Sends a single file to the Recycle Bin. Returns false if the shell refused, leaving the
    /// file untouched; callers must not fall back to a hard delete on failure.
    /// </summary>
    public static bool TryDelete(string path)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        return TryDeleteWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static bool TryDeleteWindows(string path)
    {
        // SHFileOperation wants a double-null-terminated list of paths.
        var from = path + "\0\0";

        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = from,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_NOCONFIRMMKDIR,
        };

        var result = SHFileOperation(ref operation);

        // A non-zero result or a user-abort flag both mean the file is still there.
        return result == 0 && !operation.fAnyOperationsAborted;
    }

    private const uint FO_DELETE = 0x0003;

    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMMKDIR = 0x0200;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    // Classic DllImport rather than LibraryImport: the struct carries MarshalAs string fields,
    // which the source-generated marshaller does not handle.
    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
