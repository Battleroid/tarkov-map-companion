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

    /// <summary>
    /// Quest state per character, keyed by BSG profile id.
    /// </summary>
    /// <remarks>
    /// One account has more than one. On the machine this was found on, a PVE character carried
    /// 143 quests and a PVP character carried none of them, and merging the two answered every
    /// question about the wrong one.
    /// </remarks>
    private readonly Dictionary<string, Dictionary<string, QuestProgress>> _byProfile =
        new(StringComparer.Ordinal);

    /// <summary>The character the log last loaded, and so the one being played.</summary>
    private string? _profile;

    /// <summary>
    /// Anything the logs could not attribute, which is what a pre-profile-aware cache looks like.
    /// </summary>
    private const string UnknownProfile = "";

    private Timer? _timer;
    private string? _folder;
    private bool _disposed;

    public QuestLogWatcher(TimeSpan? interval = null)
    {
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>Raised when anything about a quest changed, with the events that changed it.</summary>
    public event EventHandler<IReadOnlyList<QuestLogEvent>>? Changed;

    /// <summary>Where each quest stands for the character being played, as far as the logs know.</summary>
    /// <remarks>
    /// One character's worth, not the account's. Anything the logs could not pin to a profile is
    /// folded in underneath, so a cache written by an older build still counts for something.
    /// </remarks>
    public IReadOnlyDictionary<string, QuestProgress> State
    {
        get
        {
            lock (_gate)
            {
                var state = new Dictionary<string, QuestProgress>(StringComparer.Ordinal);

                if (_byProfile.TryGetValue(UnknownProfile, out var unattributed))
                {
                    foreach (var (id, progress) in unattributed)
                        state[id] = progress;
                }

                if (_profile is { } profile && _byProfile.TryGetValue(profile, out var mine))
                {
                    foreach (var (id, progress) in mine)
                        state[id] = progress;
                }

                return state;
            }
        }
    }

    /// <summary>Everything, by profile, for the diagnostics that have to show their work.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, QuestProgress>> ByProfile
    {
        get
        {
            lock (_gate)
            {
                return _byProfile.ToDictionary(
                    p => p.Key,
                    p => (IReadOnlyDictionary<string, QuestProgress>)
                        new Dictionary<string, QuestProgress>(p.Value, StringComparer.Ordinal),
                    StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// The character being played, as the log last reported it.
    /// </summary>
    /// <remarks>
    /// Settable because the live path learns it from the raid watcher, which is already tailing the
    /// application log where profile loads are written. Setting it swaps which character the app is
    /// answering about, so it raises <see cref="Changed"/>.
    /// </remarks>
    public string? Profile
    {
        get { lock (_gate) return _profile; }
        set
        {
            lock (_gate)
            {
                if (value is not { Length: > 0 } || string.Equals(_profile, value, StringComparison.Ordinal))
                    return;

                _profile = value;
            }

            Diagnostics.Log.Info($"[quests] following profile {value}");
            Changed?.Invoke(this, []);
        }
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
    internal void SetStateForTesting(IReadOnlyDictionary<string, QuestProgress> state, string? profile = null)
    {
        lock (_gate)
        {
            _byProfile.Clear();
            _profile = profile;

            var into = For(profile ?? UnknownProfile);

            foreach (var (id, progress) in state)
                into[id] = progress;
        }
    }

    /// <summary>The bucket for one profile, created on first use. Call under the lock.</summary>
    private Dictionary<string, QuestProgress> For(string profile)
    {
        if (!_byProfile.TryGetValue(profile, out var found))
            _byProfile[profile] = found = new Dictionary<string, QuestProgress>(StringComparer.Ordinal);

        return found;
    }

    /// <summary>Task ids the logs say are accepted and unfinished.</summary>
    public IReadOnlyList<string> Active
    {
        get
        {
            return State.Where(p => p.Value == QuestProgress.Active).Select(p => p.Key).ToArray();
        }
    }

    /// <summary>
    /// Reads every log the install has kept, then follows the newest.
    /// </summary>
    /// <param name="seed">
    /// What was worked out last time, so a cleared log folder does not lose everything already known.
    /// </param>
    public void Start(
        string logsFolder,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, QuestProgress>>? seed = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        lock (_gate)
        {
            _folder = logsFolder;
            _byProfile.Clear();

            if (seed is not null)
            {
                foreach (var (profile, state) in seed)
                {
                    var into = For(profile);

                    foreach (var (id, progress) in state)
                        into[id] = progress;
                }
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

            string? lastProfile = null;

            foreach (var file in files)
            {
                try
                {
                    // Its own launch's profile loads, read first, so every message in this file can
                    // be pinned to whoever was loaded when it arrived. The two files are written by
                    // one process against one clock, which is what makes their stamps comparable.
                    var profiles = ProfilesIn(Path.GetDirectoryName(file)!);

                    using var stream = new FileStream(
                        file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);

                    // The previous launch's last character carries into a launch whose own log says
                    // nothing: you close the game on one character and open it on the same one.
                    events.AddRange(QuestLogParser.Read(ReadLines(reader), profiles, lastProfile));

                    lastProfile = profiles.Latest ?? lastProfile;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // One unreadable log should cost its own events, not the whole history.
                }
            }

            // Whoever was loaded last across the whole history is who is being played, until the
            // live tail says otherwise.
            if (lastProfile is { Length: > 0 })
            {
                lock (_gate)
                    _profile ??= lastProfile;
            }

            var profileCount = events.Select(e => e.Profile).Where(p => p is not null).Distinct(StringComparer.Ordinal).Count();

            Diagnostics.Log.Info(
                $"[quests] read {events.Count} quest event(s) from {files.Length} log(s) "
                + $"across {profileCount} profile(s); following {_profile ?? "no profile in particular"}");
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
                var bucket = For(entry.Profile ?? _profile ?? UnknownProfile);

                // Only the ones that actually move something. A log re-read reports every event it
                // ever saw, and waking the UI for each would be a few hundred no-ops per startup.
                if (bucket.TryGetValue(entry.TaskId, out var current) && current == entry.Progress)
                    continue;

                bucket[entry.TaskId] = entry.Progress;
                changed.Add(entry);
            }
        }

        if (changed.Count > 0)
            Changed?.Invoke(this, changed);
    }

    /// <summary>The push-notification logs inside one launch folder.</summary>
    /// <summary>
    /// When each character was loaded during one launch, from that launch's application log.
    /// </summary>
    /// <remarks>
    /// The application log rather than the output log: they carry the same lines, and the raid
    /// watcher already knows how to find the application one.
    /// </remarks>
    internal static ProfileTimeline ProfilesIn(string folder)
    {
        var timeline = new ProfileTimeline();

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*application*.log", SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                using var stream = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                foreach (var line in ReadLines(reader))
                {
                    if (!line.Contains("SelectedProfile", StringComparison.Ordinal))
                        continue;

                    if (GameLogLineParser.Parse(line) is { ProfileId: { Length: > 0 } id, At: { } at })
                        timeline.Add(at.DateTime, id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No timeline is a worse answer than a timeline, not a broken one: events fall back to
            // whatever the previous launch said.
        }

        return timeline;
    }

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
