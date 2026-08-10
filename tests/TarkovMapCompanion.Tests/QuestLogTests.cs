using TarkovMapCompanion.GameLog;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Reading quest progress out of the trader chat the game logs.
/// </summary>
/// <remarks>
/// Every fixture below is a notification copied out of a real log. The backend log records that the
/// game asked the server for its quest list but never what came back -- every responseText in it is
/// empty -- so this is the only place the information exists, and its shape is undocumented and
/// entirely at BSG's discretion. Fixtures that are evidence rather than invention are the only
/// defense against that.
/// </remarks>
public sealed class QuestLogTests
{
    private static readonly string[] Started =
    [
        "2026-08-02 21:18:00.931|1.0.6.5.46221|Info|push-notifications|Got notification | ChatMessageReceived",
        "{",
        "  \"type\": \"new_message\",",
        "  \"eventId\": \"6a6fec48d8cec0cefd18aa4d\",",
        "  \"dialogId\": \"54cb57776803fa99248b456e\",",
        "  \"message\": {",
        "    \"_id\": \"6a6fec48b29e7ccb2001974a\",",
        "    \"uid\": \"54cb57776803fa99248b456e\",",
        "    \"type\": 10,",
        "    \"dt\": 1785719880,",
        "    \"text\": \"quest started\",",
        "    \"templateId\": \"657315ddab5a49b71f098853 description\",",
        "    \"hasRewards\": false,",
        "    \"maxStorageTime\": 604800",
        "  }",
        "}",
    ];

    [Fact]
    public void AStartedQuestIsRead()
    {
        var events = QuestLogParser.Read(Started);

        var entry = Assert.Single(events);
        Assert.Equal("657315ddab5a49b71f098853", entry.TaskId);
        Assert.Equal(QuestProgress.Active, entry.Progress);
        Assert.Equal(1785719880, entry.UnixSeconds);
    }

    [Fact]
    public void ACompletedQuestIsRead()
    {
        var events = QuestLogParser.Read(
        [
            "    \"dt\": 1785719999,",
            "    \"templateId\": \"5967725e86f774601a446662 successMessageText\",",
        ]);

        var entry = Assert.Single(events);
        Assert.Equal("5967725e86f774601a446662", entry.TaskId);
        Assert.Equal(QuestProgress.Completed, entry.Progress);
    }

    /// <summary>
    /// A success message sometimes carries a trader id and an index after the kind.
    /// </summary>
    /// <remarks>
    /// Seen on eight of the eighty-odd completions in the development logs. A pattern anchored at
    /// the closing quote would have read those as nothing at all, so those quests would have stayed
    /// ticked forever.
    /// </remarks>
    [Fact]
    public void ACompletionWithATrailingTraderIsStillRead()
    {
        var events = QuestLogParser.Read(
            ["    \"templateId\": \"5967733e86f774601a446663 successMessageText 58330581ace78e27b8b10cee 0\","]);

        var entry = Assert.Single(events);
        Assert.Equal("5967733e86f774601a446663", entry.TaskId);
        Assert.Equal(QuestProgress.Completed, entry.Progress);
    }

    [Fact]
    public void AFailedQuestIsRead()
    {
        var events = QuestLogParser.Read(
            ["    \"templateId\": \"5967530a86f77462ba22226b failMessageText\","]);

        Assert.Equal(QuestProgress.Failed, Assert.Single(events).Progress);
    }

    /// <summary>Ordinary trader mail is not a quest event.</summary>
    [Theory]
    [InlineData("    \"templateId\": \"5ac3475486f7741d6224abcd 0\",")]
    [InlineData("    \"text\": \"quest started\",")]
    [InlineData("2026-08-02 20:48:20.433|1.0.6.5.46221|Info|push-notifications|Received notification: Type: Ping")]
    [InlineData("")]
    public void OtherLinesAreIgnored(string line) => Assert.Empty(QuestLogParser.Read([line]));

    /// <summary>
    /// The last thing said about a quest is what counts.
    /// </summary>
    /// <remarks>
    /// Which is what makes a re-taken quest come out active. Folding these as sets instead would
    /// subtract every completion and leave it looking finished.
    /// </remarks>
    [Fact]
    public void TheLastWordWins()
    {
        var state = QuestLogParser.Fold(
        [
            new QuestLogEvent("a", QuestProgress.Active, 1, ""),
            new QuestLogEvent("a", QuestProgress.Completed, 2, ""),
            new QuestLogEvent("a", QuestProgress.Active, 3, ""),
            new QuestLogEvent("b", QuestProgress.Active, 1, ""),
            new QuestLogEvent("c", QuestProgress.Active, 1, ""),
            new QuestLogEvent("c", QuestProgress.Failed, 2, ""),
        ]);

        Assert.Equal(QuestProgress.Active, state["a"]);
        Assert.Equal(QuestProgress.Active, state["b"]);
        Assert.Equal(QuestProgress.Failed, state["c"]);
    }

    [Fact]
    public void FoldingCanContinueFromWhatWasKnown()
    {
        var seed = new Dictionary<string, QuestProgress>(StringComparer.Ordinal)
        {
            ["old"] = QuestProgress.Active,
        };

        var state = QuestLogParser.Fold([new QuestLogEvent("new", QuestProgress.Active, 1, "")], seed);

        Assert.Equal(QuestProgress.Active, state["old"]);
        Assert.Equal(QuestProgress.Active, state["new"]);
    }

    /// <summary>Several messages in one batch each get their own timestamp.</summary>
    [Fact]
    public void EachMessageKeepsItsOwnTimestamp()
    {
        var events = QuestLogParser.Read(
        [
            "    \"dt\": 100,",
            "    \"templateId\": \"657315ddab5a49b71f098853 description\",",
            "    \"dt\": 200,",
            "    \"templateId\": \"5967725e86f774601a446662 successMessageText\",",
        ]);

        Assert.Equal(2, events.Count);
        Assert.Equal(100, events[0].UnixSeconds);
        Assert.Equal(200, events[1].UnixSeconds);
    }

    // ---- The watcher --------------------------------------------------------

    [Fact]
    public void HistoryIsFoldedFromEveryLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "tmc-questlog", Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "Logs");

        try
        {
            // Two launches: the quest is taken in the first and handed in during the second, so
            // the answer is only right if both are read and in the right order.
            Write(logs, "log_a", 1, "    \"dt\": 100,\n    \"templateId\": \"657315ddab5a49b71f098853 description\",\n");
            Write(logs, "log_b", 2, "    \"dt\": 200,\n    \"templateId\": \"657315ddab5a49b71f098853 successMessageText\",\n");

            using var watcher = new QuestLogWatcher(TimeSpan.FromMinutes(10));
            watcher.Start(logs);

            Assert.Equal(QuestProgress.Completed, watcher.State["657315ddab5a49b71f098853"]);
            Assert.Empty(watcher.Active);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void ANewNotificationIsPickedUpLive()
    {
        var root = Path.Combine(Path.GetTempPath(), "tmc-questlog", Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "Logs");

        try
        {
            var file = Write(logs, "log_a", 1, "");

            using var watcher = new QuestLogWatcher(TimeSpan.FromMinutes(10));

            var seen = new List<QuestLogEvent>();
            watcher.Changed += (_, events) => seen.AddRange(events);

            watcher.Start(logs);
            Assert.Empty(seen);

            File.AppendAllText(
                file,
                "    \"dt\": 300,\n    \"templateId\": \"657315ddab5a49b71f098853 description\",\n");

            watcher.Poll();

            Assert.Equal(QuestProgress.Active, watcher.State["657315ddab5a49b71f098853"]);
            Assert.Single(seen);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>
    /// Re-reading the same events does not announce them again.
    /// </summary>
    /// <remarks>
    /// Startup folds a few hundred historical events, and waking the UI for each one that has not
    /// actually moved would be a few hundred no-op rebuilds of the quest list.
    /// </remarks>
    [Fact]
    public void OnlyRealChangesAreAnnounced()
    {
        var root = Path.Combine(Path.GetTempPath(), "tmc-questlog", Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "Logs");

        try
        {
            var file = Write(logs, "log_a", 1, "    \"dt\": 100,\n    \"templateId\": \"657315ddab5a49b71f098853 description\",\n");

            using var watcher = new QuestLogWatcher(TimeSpan.FromMinutes(10));

            var announced = 0;
            watcher.Changed += (_, events) => announced += events.Count;

            watcher.Start(logs);
            Assert.Equal(1, announced);

            // The same event again, which is what a log re-read looks like.
            File.AppendAllText(file, "    \"dt\": 100,\n    \"templateId\": \"657315ddab5a49b71f098853 description\",\n");
            watcher.Poll();

            Assert.Equal(1, announced);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>What was known survives a log folder that no longer has the history in it.</summary>
    [Fact]
    public void SeedingSurvivesAClearedFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "tmc-questlog", Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "Logs");

        try
        {
            Write(logs, "log_fresh", 1, "");

            var seed = new Dictionary<string, IReadOnlyDictionary<string, QuestProgress>>(StringComparer.Ordinal)
            {
                [""] = new Dictionary<string, QuestProgress>(StringComparer.Ordinal)
                {
                    ["657315ddab5a49b71f098853"] = QuestProgress.Active,
                },
            };

            using var watcher = new QuestLogWatcher(TimeSpan.FromMinutes(10));
            watcher.Start(logs, seed);

            Assert.Equal("657315ddab5a49b71f098853", Assert.Single(watcher.Active));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private static string Write(string logs, string launch, int minutesOld, string body)
    {
        var folder = Path.Combine(logs, launch);
        Directory.CreateDirectory(folder);

        var file = Path.Combine(folder, $"{launch} push-notifications_000.log");
        File.WriteAllText(file, body);

        // Explicit, because history is folded in write-time order and two files created in the
        // same millisecond would otherwise tie.
        File.SetLastWriteTimeUtc(file, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minutesOld));

        return file;
    }
}
