using System.Text;
using TarkovMapCompanion.GameLog;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Tailing the log while the game is writing it.
/// </summary>
/// <remarks>
/// Driven by calling <see cref="GameLogWatcher.Poll"/> directly rather than by waiting on the timer,
/// so these are deterministic. The reconcile interval is set long enough that only the sweep at
/// startup fires on its own.
/// </remarks>
public sealed class GameLogWatcherTests : IDisposable
{
    private const string Customs =
        "2026-08-03 17:45:35.616|1.1.0.0.46608|Info|application|scene preset path:maps/customs_preset.bundle rcid:bigmap.scenespreset.asset";

    private const string Woods =
        "2026-08-03 18:05:00.000|1.1.0.0.46608|Info|application|scene preset path:maps/woods_preset.bundle rcid:woods.scenespreset.asset";

    private const string Started =
        "2026-08-03 17:47:26.760|1.1.0.0.46608|Info|application|GameStarted:101.05(11.38) real:111.9(12.02) diff:10.84";

    private readonly string _logs;
    private readonly List<GameLogEvent> _seen = [];
    // A sweep interval that will never fire, so Poll is only ever called from this thread. Start
    // still attaches synchronously, which is the behavior these tests are built on.
    private readonly GameLogWatcher _watcher = new(TimeSpan.FromMinutes(10));

    public GameLogWatcherTests()
    {
        _logs = Path.Combine(Path.GetTempPath(), "tmc-logwatch", Guid.NewGuid().ToString("N"), "Logs");
        Directory.CreateDirectory(_logs);

        _watcher.EventRead += (_, e) =>
        {
            lock (_seen)
                _seen.Add(e);
        };
    }

    public void Dispose()
    {
        _watcher.Dispose();

        try { Directory.Delete(Path.GetDirectoryName(_logs)!, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private GameLogEvent[] Seen()
    {
        lock (_seen)
            return _seen.ToArray();
    }

    /// <summary>Creates a launch folder and its application log, and returns the file's path.</summary>
    private string Launch(string name, string contents = "", int written = 0)
    {
        var folder = Path.Combine(_logs, $"log_{name}");
        Directory.CreateDirectory(folder);

        var file = Path.Combine(folder, $"{name} application_000.log");
        File.WriteAllText(file, contents);

        // Explicit, because "which log is the game writing to" is decided by write time and two
        // files created in the same millisecond would otherwise tie.
        File.SetLastWriteTimeUtc(file, new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc).AddMinutes(written));

        return file;
    }

    private static void Append(string file, string line)
    {
        using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.UTF8.GetBytes(line);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Attaching to a log that already has history reports none of it.
    /// </summary>
    /// <remarks>
    /// Without this the app would walk the map through every raid of the session on startup, in the
    /// order they happened, and land on whichever was last.
    /// </remarks>
    [Fact]
    public void ExistingContentIsNotReplayed()
    {
        Launch("2026.08.03_17-00-00", $"{Customs}\n{Started}\n");

        _watcher.Start(_logs, backfill: false);
        _watcher.Poll();

        Assert.Empty(Seen());
    }

    [Fact]
    public void AnAppendedLineIsReported()
    {
        var file = Launch("2026.08.03_17-00-00", $"{Started}\n");

        _watcher.Start(_logs, backfill: false);
        _watcher.Poll();

        Append(file, $"{Customs}\n");
        _watcher.Poll();

        var seen = Seen();
        Assert.Single(seen);
        Assert.Equal(GameLogEventKind.ScenePreset, seen[0].Kind);
        Assert.Equal(["bigmap", "customs"], seen[0].MapTokens);
    }

    /// <summary>
    /// A line caught mid-write waits for its newline.
    /// </summary>
    /// <remarks>
    /// The sweep runs every couple of seconds and the game writes whenever it likes, so landing
    /// inside a line is routine rather than exotic. Reporting the half would, for a scene-preset
    /// line, mean deciding on a map from a truncated name.
    /// </remarks>
    [Fact]
    public void AHalfWrittenLineIsHeldBack()
    {
        var file = Launch("2026.08.03_17-00-00");

        _watcher.Start(_logs, backfill: false);
        _watcher.Poll();

        var split = Customs.Length - 12;

        Append(file, Customs[..split]);
        _watcher.Poll();
        Assert.Empty(Seen());

        Append(file, $"{Customs[split..]}\n");
        _watcher.Poll();

        Assert.Single(Seen());
        Assert.Equal(["bigmap", "customs"], Seen()[0].MapTokens);
    }

    /// <summary>
    /// A multi-byte character split across two reads does not come out as nonsense.
    /// </summary>
    /// <remarks>
    /// The decoder is kept across reads for this. Holding back the trailing text alone is not
    /// enough: the bytes have already been decoded by then, and half a UTF-8 sequence decodes to a
    /// replacement character that no amount of later text repairs.
    /// </remarks>
    [Fact]
    public void ASplitMultiByteCharacterSurvives()
    {
        var file = Launch("2026.08.03_17-00-00");

        _watcher.Start(_logs, backfill: false);
        _watcher.Poll();

        // A cyrillic name is not hypothetical in a Tarkov log.
        var line = "2026-08-03 17:45:35.616|1.1.0.0.46608|Info|application|Профиль scene preset "
                   + "path:maps/woods_preset.bundle rcid:woods.scenespreset.asset\n";

        var bytes = Encoding.UTF8.GetBytes(line);

        // Byte 55 lands in the middle of the first cyrillic character.
        using (var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            stream.Write(bytes, 0, 56);

        _watcher.Poll();

        using (var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            stream.Write(bytes, 56, bytes.Length - 56);

        _watcher.Poll();

        var seen = Seen();
        Assert.Single(seen);
        Assert.Contains("Профиль", seen[0].Line);
    }

    /// <summary>
    /// A new game launch creates a new folder, and that one is read from the beginning.
    /// </summary>
    /// <remarks>
    /// The opposite rule to the first attach, and deliberately: the first file is joined mid-history
    /// and every later one starts empty, so everything in it is news.
    /// </remarks>
    [Fact]
    public void ANewLaunchIsFollowedFromTheStart()
    {
        Launch("2026.08.03_17-00-00", $"{Started}\n");

        _watcher.Start(_logs, backfill: false);
        _watcher.Poll();
        Assert.Empty(Seen());

        Launch("2026.08.03_19-00-00", $"{Woods}\n", written: 120);
        _watcher.Poll();

        var seen = Seen();
        Assert.Single(seen);
        Assert.Equal(["woods"], seen[0].MapTokens);
    }

    /// <summary>A file that shrinks was replaced, so reading resumes from its start.</summary>
    [Fact]
    public void TruncationRewindsRatherThanStalls()
    {
        var file = Launch("2026.08.03_17-00-00", $"{Started}\n{Started}\n{Started}\n");

        _watcher.Start(_logs, backfill: false);
        _watcher.Poll();
        Assert.Empty(Seen());

        File.WriteAllText(file, $"{Customs}\n");
        _watcher.Poll();

        var seen = Seen();
        Assert.Single(seen);
        Assert.Equal(GameLogEventKind.ScenePreset, seen[0].Kind);
    }

    /// <summary>
    /// Starting the app during a raid picks the raid up.
    /// </summary>
    /// <remarks>
    /// The one exception to reading only what arrives after startup. Someone who launches this
    /// mid-raid is exactly the person who needs the right map.
    /// </remarks>
    [Fact]
    public void ARaidAlreadyRunningIsFoundAtStartup()
    {
        const string created =
            "2026-08-03 17:46:22.354|1.1.0.0.46608|Debug|application|TRACE-NetworkGameCreate profileStatus: "
            + "'Profileid: abc, Status: Busy, RaidMode: Online, Ip: 1.2.3.4, Port: 17003, Location: Woods, Sid: x'";

        var file = Launch("2026.08.03_17-00-00", $"{Customs}\n{created}\n{Started}\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);

        _watcher.Start(_logs);

        var seen = Seen();
        Assert.Single(seen);
        Assert.Equal(GameLogEventKind.RaidCreated, seen[0].Kind);
        Assert.Equal(["Woods"], seen[0].MapTokens);
    }

    /// <summary>
    /// A raid that has already been left is not resurrected on startup.
    /// </summary>
    /// <remarks>
    /// The rule is "a raid with nothing after it saying the player went back to the menu", not "the
    /// last raid in the file". Otherwise every startup would switch the map to whatever was played
    /// most recently, which is a different and unasked-for feature.
    /// </remarks>
    [Fact]
    public void AFinishedRaidIsNotReplayedAtStartup()
    {
        const string created =
            "2026-08-03 17:46:22.354|1.1.0.0.46608|Debug|application|TRACE-NetworkGameCreate profileStatus: "
            + "'Profileid: abc, Status: Busy, RaidMode: Online, Ip: 1.2.3.4, Port: 17003, Location: Woods, Sid: x'";

        const string menu =
            "2026-08-03 18:20:10.328|1.1.0.0.46608|Info|application|CompleteSelectedProfile ProfileId:abc AccountId:1";

        var file = Launch("2026.08.03_17-00-00", $"{created}\n{Started}\n{menu}\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);

        _watcher.Start(_logs);

        Assert.Empty(Seen());
    }

    /// <summary>
    /// Yesterday's raid is not treated as one happening now.
    /// </summary>
    /// <remarks>
    /// Without the freshness check, opening the app on a quiet afternoon would switch the map on
    /// the strength of a log line written the night before.
    /// </remarks>
    [Fact]
    public void AStaleLogIsNotBackfilled()
    {
        const string created =
            "2026-08-03 17:46:22.354|1.1.0.0.46608|Debug|application|TRACE-NetworkGameCreate profileStatus: "
            + "'Profileid: abc, Status: Busy, RaidMode: Online, Ip: 1.2.3.4, Port: 17003, Location: Woods, Sid: x'";

        var file = Launch("2026.08.03_17-00-00", $"{created}\n{Started}\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-6));

        _watcher.Start(_logs);

        Assert.Empty(Seen());
    }

    [Fact]
    public void AMissingFolderIsReportedRatherThanThrown()
    {
        var errors = new List<string>();
        _watcher.Error += (_, message) => errors.Add(message);

        _watcher.Start(Path.Combine(_logs, "nope"), backfill: false);
        _watcher.Poll();

        Assert.Single(errors);
        Assert.Empty(Seen());
        Assert.False(_watcher.IsWatching);
    }
}
