namespace TarkovMapCompanion.GameLog;

/// <summary>
/// Works out which quests are running by reading the trader chat the game logs.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the raid watcher, this one reads history on purpose. A quest accepted three sessions ago
/// is still running now, so "what is active" is the fold of every notification ever logged, not
/// just the ones that arrive while the app happens to be open.
/// </para>
/// <para>
/// It is therefore only ever as complete as the logs are. Tarkov keeps one folder per launch and
/// nothing prunes them, but a fresh install or a cleaned folder means quests taken before that look
/// untouched. That is why the answer is a suggestion the user can override rather than the truth,
/// and why what it works out is cached.
/// </para>
/// </remarks>
public sealed class QuestLogWatcher : IDisposable
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly object _pollGate = new();
    private readonly TimeSpan _interval;
    private readonly LogTail _tail = new(NewestLog);

    private readonly Dictionary<string, QuestProgress> _state = new(StringComparer.Ordinal);

    private Timer? _timer;
    private string? _folder;
    private bool _disposed;

    public QuestLogWatcher(TimeSpan? interval = null)
    {
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>Raised when anything about a quest changed, with the events that changed it.</summary>
    public event EventHandler<IReadOnlyList<QuestLogEvent>>? Changed;

    /// <summary>Where each quest stands, as far as the logs know.</summary>
    public IReadOnlyDictionary<string, QuestProgress> State
    {
        get { lock (_gate) return new Dictionary<string, QuestProgress>(_state, StringComparer.Ordinal); }
    }

    public bool IsWatching
    {
        get { lock (_gate) return _timer is not null; }
    }

    /// <summary>
    /// Puts the watcher in a known state, for tests.
    /// </summary>
    /// <remarks>
    /// The alternative is writing a folder of fake trader messages for every case, which tests the
    /// parser all over again rather than what happens downstream of it. The parser has its own
    /// tests against verbatim lines from real logs.
    /// </remarks>
    internal void SetStateForTesting(IReadOnlyDictionary<string, QuestProgress> state)
    {
        lock (_gate)
        {
            _state.Clear();

            foreach (var (id, progress) in state)
                _state[id] = progress;
        }
    }

    /// <summary>Task ids the logs say are accepted and unfinished.</summary>
    public IReadOnlyList<string> Active
    {
        get
        {
            lock (_gate)
                return _state.Where(p => p.Value == QuestProgress.Active).Select(p => p.Key).ToArray();
        }
    }

    /// <summary>
    /// Reads every log the install has kept, then follows the newest.
    /// </summary>
    /// <param name="seed">
    /// What was worked out last time, so a cleared log folder does not lose everything already known.
    /// </param>
    public void Start(string logsFolder, IReadOnlyDictionary<string, QuestProgress>? seed = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        lock (_gate)
        {
            _folder = logsFolder;
            _state.Clear();

            if (seed is not null)
            {
                foreach (var (id, progress) in seed)
                    _state[id] = progress;
            }
        }

        if (!Directory.Exists(logsFolder))
        {
            Diagnostics.Log.Warn($"[quests] log folder not found: {logsFolder}");
            return;
        }

        var fromHistory = ReadHistory(logsFolder);

        // From the start of the newest file, not its end: history has just read the whole of it,
        // and the tail's own offset needs to begin where that reading conceptually left off. Reading
        // it twice is harmless -- folding the same events again lands on the same answer.
        _tail.Reset(logsFolder, fromStart: false);

        if (fromHistory.Count > 0)
            Announce(fromHistory);

        // Attach now, not on the first tick. The tail picks its starting offset the first time it
        // is asked for lines, so leaving that to the timer means anything the game writes in the
        // meantime is seeked straight past and never read.
        Poll();

        lock (_gate)
            _timer = new Timer(_ => Poll(), null, _interval, _interval);
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

    /// <summary>Reads whatever has arrived since last time. Safe to call directly; tests do.</summary>
    public void Poll()
    {
        if (!Monitor.TryEnter(_pollGate))
            return;

        try
        {
            IReadOnlyList<string> lines;

            lock (_gate)
                lines = _folder is null ? [] : _tail.ReadNewLines();

            if (lines.Count == 0)
                return;

            var events = QuestLogParser.Read(lines);
            if (events.Count > 0)
                Announce(events);
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error("quests: sweep failed", ex);
        }
        finally
        {
            Monitor.Exit(_pollGate);
        }
    }

    /// <summary>
    /// Folds every notification log the install has kept, oldest first.
    /// </summary>
    /// <remarks>
    /// Ordered by write time rather than by folder name, for the same reason the raid watcher is:
    /// the launch folders carry an unpadded hour, so sorted as text 9am lands after 7pm and the
    /// fold would apply the day's events backwards.
    /// </remarks>
    private IReadOnlyList<QuestLogEvent> ReadHistory(string logsFolder)
    {
        var events = new List<QuestLogEvent>();

        try
        {
            var files = Directory.EnumerateDirectories(logsFolder)
                .SelectMany(NotificationLogsIn)
                .Select(path => (Path: path, Written: File.GetLastWriteTimeUtc(path)))
                .OrderBy(f => f.Written)
                .Select(f => f.Path)
                .ToArray();

            foreach (var file in files)
            {
                try
                {
                    using var stream = new FileStream(
                        file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);

                    events.AddRange(QuestLogParser.Read(ReadLines(reader)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // One unreadable log should cost its own events, not the whole history.
                }
            }

            Diagnostics.Log.Info($"[quests] read {events.Count} quest event(s) from {files.Length} log(s)");
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error("quests: could not read history", ex);
        }

        return events;
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private void Announce(IReadOnlyList<QuestLogEvent> events)
    {
        List<QuestLogEvent> changed = [];

        lock (_gate)
        {
            foreach (var entry in events)
            {
                // Only the ones that actually move something. A log re-read reports every event it
                // ever saw, and waking the UI for each would be a few hundred no-ops per startup.
                if (_state.TryGetValue(entry.TaskId, out var current) && current == entry.Progress)
                    continue;

                _state[entry.TaskId] = entry.Progress;
                changed.Add(entry);
            }
        }

        if (changed.Count > 0)
            Changed?.Invoke(this, changed);
    }

    /// <summary>The push-notification logs inside one launch folder.</summary>
    public static IReadOnlyList<string> NotificationLogsIn(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*push-notifications*.log", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The notification log the game is writing right now.</summary>
    public static string? NewestLog(string logsFolder)
    {
        try
        {
            if (!Directory.Exists(logsFolder))
                return null;

            return Directory.EnumerateDirectories(logsFolder)
                .SelectMany(NotificationLogsIn)
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

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
    }
}
