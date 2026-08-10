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
public sealed record QuestLogEvent(string TaskId, QuestProgress Progress, long UnixSeconds, string Line);

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
    public static IReadOnlyList<QuestLogEvent> Read(IEnumerable<string> lines)
    {
        var found = new List<QuestLogEvent>();
        long lastTimestamp = 0;

        foreach (var line in lines)
        {
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

            if (progress != QuestProgress.Unknown)
                found.Add(new QuestLogEvent(template.Groups["id"].Value, progress, lastTimestamp, line.Trim()));
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
