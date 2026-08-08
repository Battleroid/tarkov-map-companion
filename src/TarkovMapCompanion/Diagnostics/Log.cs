using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.Diagnostics;

/// <summary>
/// Minimal append-only log.
/// </summary>
/// <remarks>
/// Exists because a GUI app that dies has nowhere to say why: there is no console attached, and
/// the window is gone before anyone can read it. Writing to a file means a crash can be diagnosed
/// after the fact instead of guessed at.
/// </remarks>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>Kept small; this is a breadcrumb trail, not telemetry.</summary>
    private const long MaxBytes = 1024 * 1024;

    public static string Path => System.IO.Path.Combine(AppPaths.CacheDirectory, "app.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        // Mirror to stderr so `dotnet run` shows it too.
        Console.Error.WriteLine($"{level} {message}");

        try
        {
            lock (Gate)
            {
                var path = Path;

                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);

                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException)
        {
            // Logging must never be the thing that breaks the app.
        }
    }
}
