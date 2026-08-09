using TarkovMapCompanion.GameLog;
using TarkovMapCompanion.Maps;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Reading Tarkov's own log.
/// </summary>
/// <remarks>
/// Every line below was copied out of a real log on the development machine rather than written to
/// suit the parser. That distinction is the whole value of this file: the format is undocumented and
/// nothing stops a patch from changing it, so the fixtures have to be evidence.
/// </remarks>
public sealed class GameLogParserTests
{
    private const string Customs =
        "2026-08-03 17:45:35.616|1.1.0.0.46608|Info|application|scene preset path:maps/customs_preset.bundle rcid:bigmap.scenespreset.asset";

    private const string FactoryDay =
        "2026-08-04 15:59:49.025|1.1.0.0.46624|Info|application|scene preset path:maps/factory_day_preset.bundle rcid:factory_day.scenespreset.asset";

    private const string Interchange =
        "2026-08-02 20:47:48.995|1.0.6.5.46221|Info|application|scene preset path:maps/shopping_mall.bundle rcid:Shopping_Mall.ScenesPreset.asset";

    private const string GroundZeroTutorial =
        "2026-08-02 16:52:54.150|1.0.6.5.46221|Info|application|scene preset path:maps/sandbox_start_preset.bundle rcid:Sandbox_SL.ScenesPreset.asset";

    private const string RaidCreated =
        "2026-08-03 17:46:22.354|1.1.0.0.46608|Debug|application|TRACE-NetworkGameCreate profileStatus: "
        + "'Profileid: 6a6fb03826e91a600e08fed9, Status: Busy, RaidMode: Online, Ip: 178.249.210.5, "
        + "Port: 17003, Location: bigmap, Sid: US-DEN01G024_6a710bffa6f5d1832b0761b5_04.08.26_00-45-35, "
        + "GameMode: deathmatch, shortId: LH3Y7H'";

    private const string RaidStarted =
        "2026-08-03 17:47:26.760|1.1.0.0.46608|Info|application|GameStarted:101.05(11.38) real:111.9(12.02) diff:10.84";

    private const string MenuReturned =
        "2026-08-09 01:28:10.328|1.1.0.0.46657|Info|application|CompleteSelectedProfile ProfileId:6a6fb03826e91a600e08fed9 AccountId:5369768";

    [Fact]
    public void ScenePresetCarriesBothNames()
    {
        var decoded = GameLogLineParser.Parse(Customs);

        Assert.NotNull(decoded);
        Assert.Equal(GameLogEventKind.ScenePreset, decoded.Kind);

        // The resource id first, then the bundle name with its "_preset" suffix taken off.
        Assert.Equal(["bigmap", "customs"], decoded.MapTokens);
    }

    /// <summary>
    /// The scene line agrees with tarkov.dev for some maps and not others, in both directions.
    /// </summary>
    /// <remarks>
    /// This is why the event carries a list. Customs' resource id is the real location id and its
    /// bundle name is not; Ground Zero's tutorial is the other way round. Anything that picked one
    /// of the two would be choosing which maps to fail on.
    /// </remarks>
    [Fact]
    public void ScenePresetNamesAreNotInterchangeable()
    {
        // Customs: the resource id is the real location id, the bundle name is not.
        Assert.Equal(["bigmap", "customs"], GameLogLineParser.Parse(Customs)!.MapTokens);

        // Ground Zero's tutorial: the other way round.
        Assert.Equal(["Sandbox_SL", "sandbox_start"], GameLogLineParser.Parse(GroundZeroTutorial)!.MapTokens);

        // And where the two agree apart from case, only one is carried. Resolution is
        // case-insensitive throughout, so a second copy would be two lookups for one answer.
        Assert.Equal(["Shopping_Mall"], GameLogLineParser.Parse(Interchange)!.MapTokens);
        Assert.Equal(["factory_day"], GameLogLineParser.Parse(FactoryDay)!.MapTokens);
    }

    [Fact]
    public void RaidCreatedCarriesLocationAndMode()
    {
        var decoded = GameLogLineParser.Parse(RaidCreated);

        Assert.NotNull(decoded);
        Assert.Equal(GameLogEventKind.RaidCreated, decoded.Kind);
        Assert.Equal(["bigmap"], decoded.MapTokens);
        Assert.Equal("Online", decoded.RaidMode);
    }

    [Fact]
    public void RaidStartedAndMenuReturnedAreRecognized()
    {
        Assert.Equal(GameLogEventKind.RaidStarted, GameLogLineParser.Parse(RaidStarted)!.Kind);
        Assert.Equal(GameLogEventKind.MenuReturned, GameLogLineParser.Parse(MenuReturned)!.Kind);
    }

    [Fact]
    public void TimestampIsReadAsLocalTime()
    {
        var decoded = GameLogLineParser.Parse(RaidStarted);

        Assert.NotNull(decoded);
        Assert.NotNull(decoded.At);
        Assert.Equal(new DateTime(2026, 8, 3, 17, 47, 26, 760), decoded.At.Value.DateTime);
    }

    /// <summary>
    /// A line the watcher caught mid-write yields nothing rather than a wrong answer.
    /// </summary>
    /// <remarks>
    /// The watcher already holds back the fragment after the last newline, so this should never
    /// reach the parser. It is pinned anyway because "half a line parsed to the wrong map" is a
    /// failure that would present as the app switching to somewhere the player has never been.
    /// </remarks>
    [Fact]
    public void ATruncatedLineYieldsNothing()
    {
        Assert.Null(GameLogLineParser.Parse(
            "2026-08-04 15:59:49.025|1.1.0.0.46624|Info|application|scene preset path:maps/fact"));

        Assert.Null(GameLogLineParser.Parse(
            "2026-08-03 17:46:22.354|1.1.0.0.46608|Debug|application|TRACE-NetworkGameCreate profileStatus: 'Profileid: 6a6f"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2026-08-03 17:46:22.136|1.1.0.0.46608|Debug|application|TRACE-NetworkGameCreate 0")]
    [InlineData("2026-08-04 16:16:23.233|1.1.0.0.46624|Debug|application|GC::Collect")]
    [InlineData("2026-08-04 16:25:42.948|1.1.0.0.46624|Info|application|LocationLoaded:14.16 real:22.26 diff:8.1")]
    [InlineData("2026-08-04 16:16:21.531|1.1.0.0.46624|Info|application|PrepareSelectedProfileLocally ProfileId:6a6f")]
    public void OrdinaryLinesYieldNothing(string line) => Assert.Null(GameLogLineParser.Parse(line));

    /// <summary>
    /// Every name these lines carry reaches a map the app can actually draw.
    /// </summary>
    /// <remarks>
    /// The parser being right is only half of it. This is the half that catches a scene token
    /// nobody added to the alias table, which would otherwise present as the map silently not
    /// switching for one map out of thirteen.
    /// </remarks>
    [Theory]
    [InlineData(Customs, "customs")]
    [InlineData(FactoryDay, "factory")]
    [InlineData(Interchange, "interchange")]
    [InlineData(GroundZeroTutorial, "ground-zero")]
    [InlineData(RaidCreated, "customs")]
    public void EveryNameResolvesToAMap(string line, string expected)
    {
        var catalog = MapCatalog.LoadEmbedded();
        var decoded = GameLogLineParser.Parse(line);

        Assert.NotNull(decoded);

        var map = decoded.MapTokens
            .Select(catalog.ResolveByNameId)
            .FirstOrDefault(m => m is not null);

        Assert.NotNull(map);
        Assert.Equal(expected, map.NormalizedName);
    }
}
