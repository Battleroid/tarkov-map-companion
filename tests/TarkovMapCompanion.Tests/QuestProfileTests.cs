using TarkovMapCompanion.GameLog;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Whose quests these are.
/// </summary>
/// <remarks>
/// One Tarkov account has more than one character -- PVE and PVP are separate profiles with
/// separate levels and separate quests -- and the notification log carries all of them with nothing
/// in a message saying which. Reported from real logs: 143 quests and an implied level of 52 on a
/// PVE character, while the player was level 25 on the PVP one they were actually asking about.
/// </remarks>
public sealed class QuestProfileTests
{
    private const string Pve = "6a6fb03826e91a600e08fed9";
    private const string Pvp = "5fc0800c348bb4070771307f";

    private const string Courier = "60e71d6d7fcf9c556f325055";
    private const string Debut = "5936d90786f7742b1420ba5b";

    /// <summary>Verbatim shape, from a real log.</summary>
    private static string Select(string time, string profile) =>
        $"{time}|1.1.0.0.46657|Info|application|PrepareSelectedProfileLocally ProfileId:{profile} AccountId:5369768";

    private static string[] Message(string time, string taskId, string kind) =>
    [
        $"{time}|1.1.0.0.46657|Info|push-notifications|Got notification | ChatMessageReceived",
        "{",
        "  \"type\": \"new_message\",",
        "  \"message\": {",
        "    \"dt\": 1786214149,",
        $"    \"templateId\": \"{taskId} {kind}\",",
        "  }",
        "}",
    ];

    // ---- The line parser ----------------------------------------------------

    [Fact]
    public void AProfileLoadIsRecognized()
    {
        var line = Select("2026-08-09 19:13:38.528", Pve);
        var parsed = GameLogLineParser.Parse(line);

        Assert.NotNull(parsed);
        Assert.Equal(GameLogEventKind.ProfileLoaded, parsed!.Kind);
        Assert.Equal(Pve, parsed.ProfileId);
    }

    /// <summary>The menu-return line names a character too, and switching characters looks like one.</summary>
    [Fact]
    public void TheMenuReturnLineAlsoNamesTheProfile()
    {
        var line = $"2026-08-09 19:13:38.528|1.1.0.0.46657|Info|application|CompleteSelectedProfile ProfileId:{Pvp} AccountId:5369768";
        var parsed = GameLogLineParser.Parse(line);

        Assert.NotNull(parsed);
        Assert.Equal(GameLogEventKind.MenuReturned, parsed!.Kind);
        Assert.Equal(Pvp, parsed.ProfileId);
    }

    // ---- The timeline -------------------------------------------------------

    [Fact]
    public void TheTimelineAnswersWithWhoeverWasLoaded()
    {
        var timeline = new ProfileTimeline();
        timeline.Add(new DateTime(2026, 8, 9, 10, 0, 0), Pve);
        timeline.Add(new DateTime(2026, 8, 9, 14, 0, 0), Pvp);

        Assert.Equal(Pve, timeline.At(new DateTime(2026, 8, 9, 12, 0, 0)));
        Assert.Equal(Pvp, timeline.At(new DateTime(2026, 8, 9, 15, 0, 0)));
        Assert.Equal(Pvp, timeline.Latest);
    }

    /// <summary>
    /// A message before any load falls forward to the first character named.
    /// </summary>
    /// <remarks>
    /// A launch selects a profile before a trader can message you, so this is the log that begins
    /// mid-session. The first character named beats no answer at all.
    /// </remarks>
    [Fact]
    public void AMessageBeforeAnyLoadTakesTheFirstOne()
    {
        var timeline = new ProfileTimeline();
        timeline.Add(new DateTime(2026, 8, 9, 10, 0, 0), Pve);

        Assert.Equal(Pve, timeline.At(new DateTime(2026, 8, 9, 9, 0, 0)));
    }

    [Fact]
    public void AnEmptyTimelineKnowsNothing()
    {
        var timeline = new ProfileTimeline();

        Assert.Null(timeline.Latest);
        Assert.Null(timeline.At(DateTime.Now));
    }

    // ---- Attribution --------------------------------------------------------

    /// <summary>
    /// The bug, in one test: two characters, one log, and each quest goes to the right one.
    /// </summary>
    [Fact]
    public void EachMessageGoesToTheCharacterThatWasLoaded()
    {
        var timeline = new ProfileTimeline();
        timeline.Add(new DateTime(2026, 8, 9, 10, 0, 0), Pve);
        timeline.Add(new DateTime(2026, 8, 9, 14, 0, 0), Pvp);

        List<string> lines =
        [
            .. Message("2026-08-09 11:00:00.000", Courier, "successMessageText"),
            .. Message("2026-08-09 15:00:00.000", Debut, "description"),
        ];

        var events = QuestLogParser.Read(lines, timeline);

        Assert.Equal(2, events.Count);
        Assert.Equal(Pve, events[0].Profile);
        Assert.Equal(QuestProgress.Completed, events[0].Progress);
        Assert.Equal(Pvp, events[1].Profile);
        Assert.Equal(QuestProgress.Active, events[1].Progress);
    }

    /// <summary>With nothing to attribute against, events carry no profile rather than a wrong one.</summary>
    [Fact]
    public void WithNoTimelineNothingIsClaimed()
    {
        var events = QuestLogParser.Read(Message("2026-08-09 11:00:00.000", Courier, "description"));

        Assert.Null(Assert.Single(events).Profile);
    }

    /// <summary>A launch whose own log names nobody inherits the last character from the one before.</summary>
    [Fact]
    public void AnUnnamedLaunchInheritsTheLastCharacter()
    {
        var events = QuestLogParser.Read(
            Message("2026-08-09 11:00:00.000", Courier, "description"),
            new ProfileTimeline(),
            fallbackProfile: Pve);

        Assert.Equal(Pve, Assert.Single(events).Profile);
    }

    // ---- What the watcher reports -------------------------------------------

    [Fact]
    public void TheWatcherReportsOneCharacterAtATime()
    {
        using var watcher = new QuestLogWatcher(TimeSpan.FromMinutes(10));

        watcher.SetStateForTesting(new Dictionary<string, QuestProgress> { [Courier] = QuestProgress.Completed }, Pve);

        Assert.Equal(Pve, watcher.Profile);
        Assert.True(watcher.State.ContainsKey(Courier));

        // Switching characters is a profile load, and the other one's quests are not yours.
        watcher.Profile = Pvp;

        Assert.Empty(watcher.State);
        Assert.Empty(watcher.Active);
    }

    /// <summary>
    /// State the logs could not attribute counts for whoever is playing.
    /// </summary>
    /// <remarks>
    /// Which is the single-character case, and the case of a log too old to name anybody. Wrong
    /// only for the account with two characters and no attribution, where there is no better
    /// answer available anyway.
    /// </remarks>
    [Fact]
    public void UnattributedStateStillCounts()
    {
        using var watcher = new QuestLogWatcher(TimeSpan.FromMinutes(10));

        watcher.SetStateForTesting(new Dictionary<string, QuestProgress> { [Debut] = QuestProgress.Active });
        watcher.Profile = Pve;

        Assert.Equal(Debut, Assert.Single(watcher.Active));
    }
}
