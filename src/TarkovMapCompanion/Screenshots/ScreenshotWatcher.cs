namespace TarkovMapCompanion.Screenshots;

/// <summary>
/// Watches the Tarkov screenshot folder and raises <see cref="FixDetected"/> for each new capture.
/// </summary>
/// <remarks>
/// <para>
/// A bare <see cref="FileSystemWatcher"/> is not sufficient here. Its internal buffer overflows
/// silently under disk load -- exactly what happens while the game is writing a 5 MB PNG mid-raid --
/// and a dropped event means a position fix that never appears. So a periodic reconcile sweep runs
/// alongside it and picks up anything the watcher missed. Both paths funnel through the same
/// seen-set, so a file detected twice is reported once.
/// </para>
/// <para>
/// Only the filename is read, never the image, so there is no need to wait for the write to
/// finish before reporting a fix. Culling is a separate concern and retries on its own.
/// </para>
/// </remarks>
public sealed class ScreenshotWatcher : IDisposable
{
    private static readonly TimeSpan DefaultReconcileInterval = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _reconcileInterval;

    private FileSystemWatcher? _watcher;
    private Timer? _reconcileTimer;
    private string? _folder;
    private bool _disposed;

    public ScreenshotWatcher(TimeSpan? reconcileInterval = null)
    {
        _reconcileInterval = reconcileInterval ?? DefaultReconcileInterval;
    }

    /// <summary>Raised for each newly seen screenshot. May fire on a thread-pool thread.</summary>
    public event EventHandler<PlayerFix>? FixDetected;

    /// <summary>Raised when the folder cannot be watched, e.g. it does not exist.</summary>
    public event EventHandler<string>? Error;

    public string? Folder
    {
        get { lock (_gate) return _folder; }
    }

    public bool IsWatching
    {
        get { lock (_gate) return _watcher is not null; }
    }

    /// <summary>
    /// Begins watching <paramref name="folder"/>, replacing any previous target.
    /// </summary>
    /// <param name="reportExisting">
    /// When false (the default) the files already present are recorded as seen without raising
    /// events, so starting the app mid-session does not replay an entire folder of old raids.
    /// </param>
    public void Start(string folder, bool reportExisting = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        lock (_gate)
        {
            _folder = folder;
            _seen.Clear();
        }

        if (!Directory.Exists(folder))
        {
            Error?.Invoke(this, $"Screenshot folder not found: {folder}");
            return;
        }

        if (reportExisting)
        {
            foreach (var fix in ReadFolder(folder))
                Report(fix);
        }
        else
        {
            lock (_gate)
            {
                foreach (var path in ScreenshotCuller.EnumerateScreenshots(folder))
                    _seen.Add(path);
            }
        }

        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                // The game writes .png, but a filter per extension would miss a format change;
                // the name parser is the real gate.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
            };

            watcher.Created += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;

            lock (_gate)
                _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Losing the watcher is survivable: the reconcile sweep below still finds new files,
            // just up to one interval late.
            Error?.Invoke(this, $"Live folder watching unavailable ({ex.Message}); falling back to polling.");
        }

        _reconcileTimer = new Timer(_ => Reconcile(), null, _reconcileInterval, _reconcileInterval);
    }

    public void Stop()
    {
        FileSystemWatcher? watcher;
        Timer? timer;

        lock (_gate)
        {
            watcher = _watcher;
            timer = _reconcileTimer;
            _watcher = null;
            _reconcileTimer = null;
        }

        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnFileEvent;
            watcher.Renamed -= OnFileEvent;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }

        timer?.Dispose();
    }

    /// <summary>
    /// Rescans the folder for anything not yet reported. Runs on a timer, and is safe to call
    /// directly (tests drive it this way instead of waiting on wall-clock time).
    /// </summary>
    public void Reconcile()
    {
        string? folder;
        lock (_gate)
            folder = _folder;

        if (folder is null || !Directory.Exists(folder))
            return;

        foreach (var fix in ReadFolder(folder))
            Report(fix);
    }

    /// <summary>All parseable screenshots currently in a folder, oldest first.</summary>
    public static IReadOnlyList<PlayerFix> ReadFolder(string folder)
    {
        var fixes = new List<PlayerFix>();

        foreach (var path in ScreenshotCuller.EnumerateScreenshots(folder))
        {
            if (ScreenshotNameParser.TryParse(path, out var fix))
                fixes.Add(fix);
        }

        // Ordering has to fall back to the in-raid clock: filenames only carry minutes, so shots
        // taken within the same minute would otherwise come out in arbitrary order.
        return RaidSession.InChronologicalOrder(fixes);
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (ScreenshotNameParser.TryParse(e.FullPath, out var fix))
            Report(fix);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Usually a buffer overflow. The reconcile sweep covers the gap, so this is informational.
        Error?.Invoke(this, $"Folder watcher hiccup: {e.GetException().Message}");
    }

    private void Report(PlayerFix fix)
    {
        lock (_gate)
        {
            if (!_seen.Add(fix.FilePath))
                return;
        }

        FixDetected?.Invoke(this, fix);
    }

    /// <summary>
    /// Forgets a path so a file that is deleted and later recreated is reported again. Called by
    /// the culler; without it the seen-set grows for the lifetime of the session.
    /// </summary>
    public void Forget(string path)
    {
        lock (_gate)
            _seen.Remove(path);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
    }
}
