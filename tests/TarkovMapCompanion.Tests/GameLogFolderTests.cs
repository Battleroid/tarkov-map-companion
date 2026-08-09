using TarkovMapCompanion.GameLog;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Finding where Tarkov is installed.
/// </summary>
/// <remarks>
/// The launcher will install the game anywhere, the registry is frequently absent, and the logs sit
/// under the install rather than in AppData. So this is the same shape as the screenshot folder
/// search, and it fails the same quiet way when it is wrong.
/// </remarks>
public sealed class GameLogFolderTests : IDisposable
{
    private readonly string _root;

    public GameLogFolderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tmc-gamelogs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    /// <summary>Creates an install root, optionally with launch folders holding logs in it.</summary>
    private string Install(string name, int launches = 0, bool exists = true)
    {
        var install = Path.Combine(_root, name);

        if (!exists)
            return install;

        var logs = Path.Combine(install, GameLogFolders.LogsFolderName);
        Directory.CreateDirectory(logs);

        for (var i = 0; i < launches; i++)
        {
            var launch = Path.Combine(logs, $"log_2026.08.0{i + 1}_12-00-00_1.1.0.0.46657");
            Directory.CreateDirectory(launch);
            File.WriteAllText(Path.Combine(launch, $"2026.08.0{i + 1} application_000.log"), "");
        }

        return install;
    }

    [Fact]
    public void LogsPresentBeatsAFolderThatMerelyExists()
    {
        var empty = Install("empty");
        var real = Install("real", launches: 3);

        var found = GameLogFolders.Evaluate([(empty, "first"), (real, "second")]);

        Assert.Equal(Path.Combine(real, "Logs"), found[0].Path);
        Assert.True(found[0].Looks);
        Assert.Equal(3, found[0].LogFolderCount);
    }

    [Fact]
    public void AFolderThatExistsBeatsOneThatDoesNot()
    {
        var missing = Install("missing", exists: false);
        var empty = Install("empty");

        var found = GameLogFolders.Evaluate([(missing, "first"), (empty, "second")]);

        Assert.Equal(Path.Combine(empty, "Logs"), found[0].Path);
        Assert.False(found[0].Looks);
        Assert.True(found[0].Exists);
    }

    /// <summary>
    /// A launch folder with no application log in it does not count as evidence.
    /// </summary>
    /// <remarks>
    /// The game creates the folder before it writes anything into it, so counting bare directories
    /// would report an install as usable during the seconds before its first log line.
    /// </remarks>
    [Fact]
    public void AnEmptyLaunchFolderIsNotEvidence()
    {
        var install = Install("bare");
        Directory.CreateDirectory(Path.Combine(install, "Logs", "log_2026.08.09_12-00-00_1.1.0.0.46657"));

        var found = GameLogFolders.Evaluate([(install, "only")]);

        Assert.True(found[0].Exists);
        Assert.False(found[0].Looks);
        Assert.Equal(0, found[0].LogFolderCount);
    }

    [Fact]
    public void TheSameRootIsOnlyConsideredOnce()
    {
        var install = Install("once", launches: 1);

        var found = GameLogFolders.Evaluate([(install, "first"), (install, "second"), (null, "nothing")]);

        Assert.Single(found);
        Assert.Equal("first", found[0].Source);
    }

    // ---- Reading a path out of a log ----------------------------------------

    /// <summary>
    /// The line the game itself writes, verbatim from this machine's Player.log.
    /// </summary>
    [Fact]
    public void ThePathIsReadOutOfTheUnityLog()
    {
        const string line =
            "[Subsystems] Discovering subsystems at path A:/Other/Tarkov/EscapeFromTarkov_Data/UnitySubsystems";

        Assert.Equal(["A:/Other/Tarkov"], GameLogFolders.InstallRootsIn(line).ToArray());
    }

    /// <summary>
    /// Two paths on one line still yields the right one.
    /// </summary>
    /// <remarks>
    /// This is the case that decided the whole approach. The launcher writes prose between two
    /// absolute paths, and a regex reading forward from the first drive letter swallows the middle
    /// and reports a root that never existed. Walking back from the marker to the nearest drive
    /// colon is what makes it come out right, and it is not obvious enough to leave untested.
    /// </remarks>
    [Fact]
    public void TwoPathsOnOneLineDoNotRunTogether()
    {
        const string line =
            @"Applying A:\Other\BsgLauncher\Temp\eft_live.bsgp to directory "
            + @"A:\Other\Tarkov\EscapeFromTarkov_Data\ScriptingAssemblies.json deleted";

        Assert.Equal([@"A:\Other\Tarkov"], GameLogFolders.InstallRootsIn(line).ToArray());
    }

    /// <summary>An install under Program Files has a space in it and must survive whole.</summary>
    [Fact]
    public void APathWithSpacesSurvives()
    {
        const string line = @"Mono path[0] = 'C:\Program Files\Battlestate Games\EFT\EscapeFromTarkov_Data\Managed'";

        Assert.Equal([@"C:\Program Files\Battlestate Games\EFT"], GameLogFolders.InstallRootsIn(line).ToArray());
    }

    [Fact]
    public void EachRootIsReportedOnce()
    {
        var text = string.Join(
            '\n',
            "at path A:/Other/Tarkov/EscapeFromTarkov_Data/UnitySubsystems",
            "loading A:/Other/Tarkov/EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll",
            "other D:/Games/EFT/EscapeFromTarkov_Data/boot.config");

        Assert.Equal(["A:/Other/Tarkov", "D:/Games/EFT"], GameLogFolders.InstallRootsIn(text).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nothing interesting here at all")]
    [InlineData("EscapeFromTarkov_Data with no path in front of it")]
    [InlineData("relative/EscapeFromTarkov_Data/Managed")]
    public void TextWithNoInstallPathYieldsNothing(string? text) =>
        Assert.Empty(GameLogFolders.InstallRootsIn(text));
}
