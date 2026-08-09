using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// The whole path from a line in Tarkov's log to the session acting on it.
/// </summary>
/// <remarks>
/// The parser and the watcher are covered on their own; this is the glue between them and the app,
/// which is short enough to look obviously correct and has the same failure mode as everything else
/// here: it does nothing, quietly. Real lines again, driven through a real session with a real
/// watcher over a temporary folder.
/// </remarks>
public sealed class GameLogSessionTests : IDisposable
{
    private const string WoodsLoading =
        "2026-08-03 18:05:00.000|1.1.0.0.46608|Info|application|scene preset path:maps/woods_preset.bundle rcid:woods.scenespreset.asset";

    private const string InterchangeLoading =
        "2026-08-02 20:47:48.995|1.0.6.5.46221|Info|application|scene preset path:maps/shopping_mall.bundle rcid:Shopping_Mall.ScenesPreset.asset";

    private const string RaidStarted =
        "2026-08-03 18:07:26.760|1.1.0.0.46608|Info|application|GameStarted:101.05(11.38) real:111.9(12.02) diff:10.84";

    private const string MenuReturned =
        "2026-08-03 18:48:10.328|1.1.0.0.46608|Info|application|CompleteSelectedProfile ProfileId:abc AccountId:1";

    private readonly string _root;
    private readonly string _logs;
    private readonly string _file;

    public GameLogSessionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tmc-logsession", Guid.NewGuid().ToString("N"));

        _logs = Path.Combine(_root, "Logs");
        var launch = Path.Combine(_logs, "log_2026.08.03_18-00-00_1.1.0.0.46608");
        Directory.CreateDirectory(launch);

        _file = Path.Combine(launch, "2026.08.03_18-00-00 application_000.log");
        File.WriteAllText(_file, "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private MapSession NewSession(bool readGameLog = true)
    {
        var settings = new AppSettings
        {
            // No network and no screenshot watching: this test is about the log and nothing else.
            AllowNetwork = false,
            ReadGameLog = readGameLog,
            GameLogFolder = _logs,
            CurrentMap = "customs",
            ScreenshotFolder = Path.Combine(_root, "shots"),
            CullMode = CullMode.Off,
        };

        return new MapSession(settings, MapCatalog.LoadEmbedded());
    }

    private void Append(string line) => File.AppendAllText(_file, $"{line}\n");

    [Fact]
    public void TheMapTheGameIsLoadingIsAnnounced()
    {
        using var session = NewSession();

        GameMap? detected = null;
        session.MapDetectedFromLog += (_, map) => detected = map;

        session.StartWatchingGameLog();

        Append(WoodsLoading);
        session.GameLog.Poll();

        Assert.NotNull(detected);
        Assert.Equal("woods", detected.NormalizedName);
    }

    /// <summary>
    /// The scene name that is not a tarkov.dev location id still lands on the right map.
    /// </summary>
    /// <remarks>
    /// Interchange is the case that would break a build treating the log's resource id as the
    /// location id, and it would break for exactly one map out of thirteen.
    /// </remarks>
    [Fact]
    public void AnAwkwardSceneNameStillResolves()
    {
        using var session = NewSession();

        GameMap? detected = null;
        session.MapDetectedFromLog += (_, map) => detected = map;

        session.StartWatchingGameLog();

        Append(InterchangeLoading);
        session.GameLog.Poll();

        Assert.Equal("interchange", detected?.NormalizedName);
    }

    /// <summary>The map already being shown is not announced again.</summary>
    [Fact]
    public void TheMapAlreadyShownIsNotAnnounced()
    {
        using var session = NewSession();

        var announced = 0;
        session.MapDetectedFromLog += (_, _) => announced++;

        session.StartWatchingGameLog();

        Append("2026-08-03 18:05:00.000|1.1.0.0.46608|Info|application|"
               + "scene preset path:maps/customs_preset.bundle rcid:bigmap.scenespreset.asset");

        session.GameLog.Poll();

        Assert.Equal(0, announced);
    }

    /// <summary>
    /// Turning the setting off means the file is not opened at all.
    /// </summary>
    /// <remarks>
    /// The point of the preference is that it is a real opt-in, not a filter applied after reading.
    /// </remarks>
    [Fact]
    public void NothingIsReadWhenTheSettingIsOff()
    {
        using var session = NewSession(readGameLog: false);

        var announced = 0;
        session.MapDetectedFromLog += (_, _) => announced++;

        session.StartWatchingGameLog();

        Append(WoodsLoading);
        session.GameLog.Poll();

        Assert.Equal(0, announced);
        Assert.Null(session.GameLog.Folder);
    }

    [Fact]
    public void ARaidStartingClearsTheTrail()
    {
        using var session = NewSession();

        var states = new List<bool>();
        session.RaidStateChanged += (_, started) => states.Add(started);

        session.StartWatchingGameLog();

        session.Player.Add(new Screenshots.PlayerFix
        {
            FilePath = "x.png",
            Position = new GamePosition(1, 2, 3),
            YawDegrees = 0,
            Rotation = (0, 0, 0, 1),
            TakenAt = DateTime.Now,
            RaidTimeHours = 10.0,
        });

        Assert.NotNull(session.Player.Current);

        Append(RaidStarted);
        session.GameLog.Poll();

        Assert.Equal([true], states);
        Assert.Null(session.Player.Current);
    }

    /// <summary>
    /// Getting back to the menu is reported, but only after a raid was known to be running.
    /// </summary>
    /// <remarks>
    /// The line the game writes on the way out is the same one it writes on the way in, and it
    /// writes it twice on the way out. Without the guard the app would announce the end of a raid
    /// that never started, every time the profile loaded.
    /// </remarks>
    [Fact]
    public void TheMenuIsOnlyReportedAfterARaid()
    {
        using var session = NewSession();

        var states = new List<bool>();
        session.RaidStateChanged += (_, started) => states.Add(started);

        session.StartWatchingGameLog();

        Append(MenuReturned);
        session.GameLog.Poll();
        Assert.Empty(states);

        Append(RaidStarted);
        Append(MenuReturned);
        Append(MenuReturned);
        session.GameLog.Poll();

        Assert.Equal([true, false], states);
    }

    /// <summary>
    /// A location this build has never heard of changes nothing.
    /// </summary>
    /// <remarks>
    /// The failure that matters after a Tarkov patch. Switching to whatever sorts first would be
    /// far worse than not switching, so the only trace is a line in the log.
    /// </remarks>
    [Fact]
    public void AnUnknownLocationIsIgnored()
    {
        using var session = NewSession();

        var announced = 0;
        session.MapDetectedFromLog += (_, _) => announced++;

        session.StartWatchingGameLog();

        Append("2026-08-03 18:05:00.000|1.1.0.0.46608|Info|application|"
               + "scene preset path:maps/somewhere_new_preset.bundle rcid:SomewhereNew.ScenesPreset.asset");

        session.GameLog.Poll();

        Assert.Equal(0, announced);
        Assert.Equal("customs", session.CurrentMap.NormalizedName);
    }
}
