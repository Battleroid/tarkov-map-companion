using System.Globalization;
using System.Text.RegularExpressions;

namespace TarkovMapCompanion.GameLog;

/// <summary>What the game last said about a quest.</summary>
public enum QuestProgress
{
    /// <summary>Nothing in the logs mentions it.</summary>
    Unknown = 0,

    /// <summary>Accepted from the trader and not yet finished.</summary>
    Active,

    /// <summary>Handed in.</summary>
    Completed,

    /// <summary>Failed, whether by timer, by a kill, or by choice.</summary>
    Failed,
}

/// <summary>One thing the game said about one quest, at a point in time.</summary>
/// <param name="Profile">
/// The character it happened to, when that is knowable, and null when it is not.
/// </param>
/// <remarks>
/// The profile is the difference between "you have 143 quests and are at least level 52" and the
/// truth, which on the machine this was found on was two characters -- a PVE one with all of that
/// history and a PVP one with none of it. Nothing in a trader message says which character it
/// belongs to; it has to come from the profile that was loaded at the time.
/// </remarks>
public sealed record QuestLogEvent(
    string TaskId,
    QuestProgress Progress,
    long UnixSeconds,
    string Line,
    string? Profile = null);

/// <summary>
/// Which character was loaded when, so a message can be attributed to one.
/// </summary>
/// <remarks>
/// Built from the profile-select lines in a launch's own application log, and consulted by the
/// wall-clock stamp every log line begins with. Both files are written by the same process against
/// the same clock, which is what makes comparing their timestamps meaningful.
/// </remarks>
public sealed class ProfileTimeline
{
    private readonly List<(DateTime At, string Profile)> _loads = [];

    public int Count => _loads.Count;

    /// <summary>The last character loaded, which is the one being played.</summary>
    public string? Latest => _loads.Count == 0 ? null : _loads[^1].Profile;

    public void Add(DateTime at, string profile)
    {
        _loads.Add((at, profile));

        // Log lines arrive in order, so this is a no-op in the normal case and cheap insurance in
        // the abnormal one -- reading two files out of order, say.
        if (_loads.Count > 1 && _loads[^2].At > at)
            _loads.Sort((a, b) => a.At.CompareTo(b.At));
    }

    /// <summary>
    /// The character loaded at a moment.
    /// </summary>
    /// <remarks>
    /// Falls forward to the first load when asked about a time before any of them. A launch selects
    /// a profile before any trader message can arrive, but a log that begins mid-session has
    /// messages with nothing in front of them, and the first character named is a better guess
    /// than none.
    /// </remarks>
    public string? At(DateTime when)
    {
        string? found = null;

        foreach (var (at, profile) in _loads)
        {
            if (at > when)
                break;

            found = profile;
        }

        return found ?? (_loads.Count > 0 ? _loads[0].Profile : null);
    }
}

/// <summary>
/// Reads quest progress out of the trader chat the game logs.
/// </summary>
/// <remarks>
/// <para>
/// The game asks the server for the quest list over HTTPS, and the backend log records that it
/// happened but not what came back: every <c>responseText:</c> in it is empty. What is not empty is
/// the push-notification log, where trader messages arrive in full, and a quest changing state is a
/// trader message. The task id rides along in <c>templateId</c>.
/// </para>
/// <para>
/// Three shapes matter, all confirmed against real logs:
/// </para>
/// <list type="bullet">
///   <item><description><c>"&lt;id&gt; description"</c> with text "quest started" -- accepted.</description></item>
///   <item><description><c>"&lt;id&gt; successMessageText"</c> -- handed in. Sometimes carries a
///     trailing trader id and index, which is why the pattern does not anchor at the end.</description></item>
///   <item><description><c>"&lt;id&gt; failMessageText"</c> -- failed.</description></item>
/// </list>
/// <para>
/// Pure and line-based. The notification is a pretty-printed JSON block spanning many lines, but
/// the two fields needed never share a line and always arrive in the same order, so a full JSON
/// parse buys nothing over remembering the last timestamp seen.
/// </para>
/// </remarks>
public static partial class QuestLogParser
{
    /// <summary>
    /// Folds a log's worth of lines into one event per state change, oldest first.
    /// </summary>
    /// <remarks>
    /// Stateful across calls in the caller, not here: feed it each batch of new lines and it
    /// reports only what those lines said.
    /// </remarks>
    public static IReadOnlyList<QuestLogEvent> Read(
        IEnumerable<string> lines,
        ProfileTimeline? profiles = null,
        string? fallbackProfile = null)
    {
        var found = new List<QuestLogEvent>();
        long lastTimestamp = 0;
        DateTime? lastLineTime = null;

        foreach (var line in lines)
        {
            // The line's own clock, which is the one the application log shares. The message's dt
            // is the server's and has nothing to compare against.
            if (ReadLineTime(line) is { } lineTime)
                lastLineTime = lineTime;

            if (TimestampPattern().Match(line) is { Success: true } stamp
                && long.TryParse(stamp.Groups["dt"].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dt))
            {
                lastTimestamp = dt;
                continue;
            }

            if (TemplatePattern().Match(line) is not { Success: true } template)
                continue;

            var progress = template.Groups["kind"].Value switch
            {
                "description" => QuestProgress.Active,
                "successMessageText" => QuestProgress.Completed,
                "failMessageText" => QuestProgress.Failed,
                _ => QuestProgress.Unknown,
            };

            if (progress == QuestProgress.Unknown)
                continue;

            var profile = (lastLineTime is { } when ? profiles?.At(when) : null)
                          ?? profiles?.Latest
                          ?? fallbackProfile;

            found.Add(new QuestLogEvent(
                template.Groups["id"].Value, progress, lastTimestamp, line.Trim(), profile));
        }

        return found;
    }

    /// <summary>
    /// Reduces a stream of events to where each quest ended up.
    /// </summary>
    /// <remarks>
    /// Last word wins, which is what makes a re-taken quest come out active rather than completed.
    /// The caller is responsible for feeding these in chronological order; the timestamps are only
    /// there so it can.
    /// </remarks>
    public static Dictionary<string, QuestProgress> Fold(
        IEnumerable<QuestLogEvent> events,
        Dictionary<string, QuestProgress>? onto = null)
    {
        var state = onto ?? new Dictionary<string, QuestProgress>(StringComparer.Ordinal);

        foreach (var entry in events)
            state[entry.TaskId] = entry.Progress;

        return state;
    }

    /// <summary>Reads a log line's leading wall-clock stamp, or null when it has none.</summary>
    public static DateTime? ReadLineTime(string line)
    {
        if (line.Length < 23)
            return null;

        return DateTime.TryParseExact(
            line.AsSpan(0, 23),
            "yyyy-MM-dd HH:mm:ss.fff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
    }

    // The unix seconds the message carries, which is the game's own ordering rather than the log
    // line's clock.
    [GeneratedRegex(@"""dt""\s*:\s*(?<dt>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();

    // Not anchored at the end: a success message sometimes carries a trader id and an index after
    // the kind, e.g. "<id> successMessageText 58330581ace78e27b8b10cee 0".
    [GeneratedRegex(
        @"""templateId""\s*:\s*""(?<id>[0-9a-f]{24})\s+(?<kind>description|successMessageText|failMessageText)",
        RegexOptions.CultureInvariant)]
    private static partial Regex TemplatePattern();
}
