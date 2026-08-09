using System.Text;

namespace TarkovMapCompanion.GameLog;

/// <summary>
/// Tails the log Escape from Tarkov is writing right now and reports what it says.
/// </summary>
/// <remarks>
/// <para>
/// A peer of <c>ScreenshotWatcher</c>, with one deliberate difference: no
/// <see cref="FileSystemWatcher"/>, just the periodic sweep. The screenshot folder has one because
/// the marker should move the instant you press the key. Nothing here is like that. The whole value
/// of this class is a head start of twenty seconds to two minutes on the map that is loading, and a
/// sweep that can be up to two seconds late spends none of it, while a second thread reading the
/// same file position introduces a race worth more than it saves.
/// </para>
/// <para>
/// Nothing here writes, injects, or reads memory. It opens a text file the game has already written
/// to disk, with sharing flags that let the game keep its own handle, and reads four kinds of line
/// out of it. It is the same relationship the app already has with the screenshot folder.
/// </para>
/// <para>
/// Three details are load-bearing and were each learned the hard way somewhere:
/// </para>
/// <list type="bullet">
///   <item><description>A new folder appears under <c>Logs</c> on every game launch, and the file
///     inside it rolls from <c>_000</c> to <c>_001</c> in a long session. Following the newest
///     application log by write time handles both without special-casing either, which is also
///     why the sweep looks the folder up again every time rather than holding a handle.</description></item>
///   <item><description>A read almost always lands mid-line. The trailing fragment is held back
///     until its newline arrives, and the UTF-8 decoder is kept across reads so a multi-byte
///     character split across the boundary does not decode to nonsense.</description></item>
///   <item><description>Starting at the end of the file is right, except when the app is launched
///     during a raid that is already running. Hence the one backward scan at startup.</description></item>
/// </list>
/// </remarks>
public sealed class GameLogWatcher : IDisposable
{
    private static readonly TimeSpan DefaultReconcileInterval = TimeSpan.FromSeconds(2);

    /// <summary>How far back to look for a raid already in progress, in bytes.</summary>
    private const int BackfillBytes = 512 * 1024;

    /// <summary>
    /// How stale the newest log may be and still be worth backfilling from.
    /// </summary>
    /// <remarks>
    /// Without this, launching the app on a quiet afternoon would switch the map to whatever was
    /// played last night, on the strength of a log line hours old. The point of the backfill is a
    /// raid happening right now.
    /// </remarks>
    private static readonly TimeSpan BackfillFreshness = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly object _pollGate = new();
    private readonly TimeSpan _reconcileInterval;

    private Timer? _timer;

    private string? _folder;
    private string? _currentFile;
    private long _offset;
    private string _partialLine = "";
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private bool _attached;
    private bool _disposed;

    public GameLogWatcher(TimeSpan? reconcileInterval = null)
    {
        _reconcileInterval = reconcileInterval ?? DefaultReconcileInterval;
    }

    /// <summary>Raised for each recognized line. May fire on a thread-pool thread.</summary>
    public event EventHandler<GameLogEvent>? EventRead;

    /// <summary>Raised when the logs cannot be followed. Informational; the app works without it.</summary>
    public event EventHandler<string>? Error;

    /// <summary>The <c>Logs</c> directory being followed, or null.</summary>
    public string? Folder
    {
        get { lock (_gate) return _folder; }
    }

    /// <summary>The application log currently being tailed, for diagnostics.</summary>
    public string? CurrentFile
    {
        get { lock (_gate) return _currentFile; }
    }

    public bool IsWatching
    {
        get { lock (_gate) return _timer is not null; }
    }

    /// <summary>
    /// Begins following <paramref name="logsFolder"/>, replacing any previous target.
    /// </summary>
    /// <param name="backfill">
    /// When true (the default) the tail of the newest log is scanned once for a raid that is
    /// already under way, so starting the app mid-raid still lands on the right map.
    /// </param>
    public void Start(string logsFolder, bool backfill = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        lock (_gate)
        {
            _folder = logsFolder;
            _currentFile = null;
            _offset = 0;
            _partialLine = "";
            _decoder = Encoding.UTF8.GetDecoder();
            _attached = false;
        }

        if (!Directory.Exists(logsFolder))
        {
            Error?.Invoke(this, $"Tarkov log folder not found: {logsFolder}");
            return;
        }

        // Before attaching, not after. The first sweep seeks to the end of the file, and a backfill
        // racing it would report the raid it found after the live reader had already moved past it.
        if (backfill)
            Backfill();

        // Attach synchronously rather than letting the timer's first tick do it. Otherwise Start
        // returns before the watcher has decided where in the file it is, and whatever the game
        // writes in that window is either read twice or missed depending on which thread wins.
        Poll();

        var timer = new Timer(_ => Poll(), null, _reconcileInterval, _reconcileInterval);

        lock (_gate)
            _timer = timer;
    }

    public void Stop()
    {
        Timer? timer;

        lock (_gate)
        {
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    /// <summary>
    /// Reads whatever has been appended since last time. Runs on a timer, and is safe to call
    /// directly; tests drive it this way rather than waiting on wall-clock time.
    /// </summary>
    public void Poll()
    {
        // A timer callback, with nowhere for an exception to go. Overlapping calls skip rather than
        // queue: the next tick is two seconds away, and two readers sharing one file position
        // divide the bytes between them rather than each seeing all of them.
        if (!Monitor.TryEnter(_pollGate))
            return;

        try
        {
            foreach (var read in ReadNewLines())
            {
                if (GameLogLineParser.Parse(read) is { } decoded)
                    EventRead?.Invoke(this, decoded);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error("game log: sweep failed", ex);
        }
        finally
        {
            Monitor.Exit(_pollGate);
        }
    }

    /// <summary>The newest application log under a Logs folder, by write time.</summary>
    /// <remarks>
    /// By write time rather than by name, because the launch folders are named
    /// <c>log_2026.08.09_1-27-19</c> with an unpadded hour: sorted as text, 9am lands after 7pm.
    /// </remarks>
    public static string? NewestLog(string logsFolder)
    {
        try
        {
            if (!Directory.Exists(logsFolder))
                return null;

            return Directory.EnumerateDirectories(logsFolder)
                .SelectMany(GameLogFolders.ApplicationLogsIn)
                .Select(path => (Path: path, Written: File.GetLastWriteTimeUtc(path)))
                .OrderByDescending(f => f.Written)
                .Select(f => f.Path)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Looks for a raid that started before the app did.
    /// </summary>
    /// <remarks>
    /// Only the raid-created line, and only when nothing later says the player went back to the
    /// menu. Replaying the whole history instead would walk the map through every raid of the
    /// session on startup.
    /// </remarks>
    private void Backfill()
    {
        try
        {
            string? folder;
            lock (_gate)
                folder = _folder;

            if (folder is null || NewestLog(folder) is not { } file)
                return;

            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > BackfillFreshness)
                return;

            GameLogEvent? started = null;

            foreach (var line in ReadTail(file, BackfillBytes))
            {
                switch (GameLogLineParser.Parse(line))
                {
                    case { Kind: GameLogEventKind.RaidCreated } created:
                        started = created;
                        break;

                    case { Kind: GameLogEventKind.MenuReturned }:
                        started = null;
                        break;
                }
            }

            if (started is not null)
            {
                Diagnostics.Log.Info($"[game log] a raid was already running when the app started: {started.Line}");
                EventRead?.Invoke(this, started);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error("game log: backfill failed", ex);
        }
    }

    /// <summary>
    /// Complete lines appended since the last call, following a rollover if one happened.
    /// </summary>
    private IReadOnlyList<string> ReadNewLines()
    {
        string? folder;
        lock (_gate)
            folder = _folder;

        if (folder is null)
            return [];

        var newest = NewestLog(folder);
        if (newest is null)
            return [];

        lock (_gate)
        {
            if (!string.Equals(newest, _currentFile, StringComparison.OrdinalIgnoreCase))
            {
                // The first file gets picked up mid-history, so start at its end and let the one
                // backward scan in Backfill decide whether a raid is already under way. Every later
                // file is a fresh launch or a rolled suffix, which we do want from byte zero.
                _offset = _attached ? 0 : SafeLength(newest);
                _currentFile = newest;
                _partialLine = "";
                _decoder = Encoding.UTF8.GetDecoder();
                _attached = true;
            }
        }

        return ReadFrom(newest);
    }

    private IReadOnlyList<string> ReadFrom(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            long start;
            lock (_gate)
            {
                // A shorter file than last time means it was truncated or replaced under us.
                if (stream.Length < _offset)
                {
                    _offset = 0;
                    _partialLine = "";
                    _decoder = Encoding.UTF8.GetDecoder();
                }

                start = _offset;
            }

            if (stream.Length <= start)
                return [];

            stream.Seek(start, SeekOrigin.Begin);

            var buffer = new byte[stream.Length - start];
            var read = stream.Read(buffer, 0, buffer.Length);

            lock (_gate)
            {
                _offset = start + read;

                var chars = new char[_decoder.GetCharCount(buffer, 0, read, flush: false)];
                _decoder.GetChars(buffer, 0, read, chars, 0, flush: false);

                var text = _partialLine + new string(chars);
                var lines = text.Split('\n');

                // The last element is whatever came after the final newline. Held back rather than
                // reported: the game writes a line in pieces, and half of one parses to nothing at
                // best and to the wrong map at worst.
                _partialLine = lines[^1];

                return lines[..^1].Select(l => l.TrimEnd('\r')).ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The game holds this file open and rotates it out from under us; both are ordinary.
            return [];
        }
    }

    /// <summary>The last <paramref name="bytes"/> of a file, as complete lines.</summary>
    private static IReadOnlyList<string> ReadTail(string path, int bytes)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var length = Math.Min(bytes, stream.Length);
            stream.Seek(stream.Length - length, SeekOrigin.Begin);

            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, buffer.Length);

            var lines = Encoding.UTF8.GetString(buffer, 0, read).Split('\n');

            // The first line is very likely cut in half by where the seek landed.
            return lines.Length < 2 ? [] : lines[1..].Select(l => l.TrimEnd('\r')).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
    }
}
