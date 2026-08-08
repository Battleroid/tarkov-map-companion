using TarkovMapCompanion.Screenshots;
using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// The culler is the only thing in the app that destroys user data, so these tests lean hard on
/// what it must refuse. Nothing here touches the real Recycle Bin: deletion is intercepted.
/// </summary>
public sealed class ScreenshotCullerTests : IDisposable
{
    private readonly string _folder;
    private readonly List<string> _deleted = [];

    public ScreenshotCullerTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "tmc-cull", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private ScreenshotCuller Culler(AppSettings settings) =>
        new(settings)
        {
            DeleteOverride = path =>
            {
                _deleted.Add(path);
                File.Delete(path);
                return true;
            },
        };

    /// <summary>Writes a screenshot whose name encodes <paramref name="minute"/> and back-dates it.</summary>
    private string WriteShot(int minute)
    {
        var name = $"2026-08-07[19-{minute:00}]_-720.10, -48.62, 430.51_0, 0.66069, 0, -0.74938_11.67 (0).png";
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "not really a png");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 7, 19, minute, 0, DateTimeKind.Utc));
        return path;
    }

    private string WriteOther(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "someone else's file");
        return path;
    }

    // ---- Refusals ----------------------------------------------------------

    [Fact]
    public void Off_ByDefault_DeletesNothingEvenWithAFullFolder()
    {
        for (var i = 0; i < 50; i++)
            WriteShot(i);

        var results = Culler(new AppSettings()).Apply(_folder);

        Assert.Empty(results);
        Assert.Empty(_deleted);
        Assert.Equal(50, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public void KeepLatest_NeverTouchesFilesItDoesNotRecognise()
    {
        var notes = WriteOther("important-notes.png");
        var config = WriteOther("settings.json");
        var almost = WriteOther("2026-08-07 screenshot.png");

        for (var i = 0; i < 30; i++)
            WriteShot(i);

        var settings = new AppSettings { CullMode = CullMode.KeepLatest, CullKeepCount = 5 };
        Culler(settings).Apply(_folder);

        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(config));
        Assert.True(File.Exists(almost));
        Assert.All(_deleted, path => Assert.True(ScreenshotNameParser.IsScreenshotFileName(path)));
    }

    [Fact]
    public void DeleteAfterRead_RefusesAPathOutsideTheWatchedFolder()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "tmc-cull-outside");
        Directory.CreateDirectory(elsewhere);
        var victim = Path.Combine(elsewhere,
            "2026-08-07[19-31]_-720.10, -48.62, 430.51_0, 0.66069, 0, -0.74938_11.67 (0).png");
        File.WriteAllText(victim, "x");

        try
        {
            var settings = new AppSettings { CullMode = CullMode.DeleteAfterRead };
            var results = Culler(settings).Apply(_folder, victim);

            Assert.Equal(CullRefusal.OutsideWatchedFolder, results.Single().Refusal);
            Assert.True(File.Exists(victim));
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public void DeleteAfterRead_RefusesATraversalPathThatResolvesOutsideTheFolder()
    {
        var escape = Path.Combine(_folder, "..",
            "2026-08-07[19-31]_-720.10, -48.62, 430.51_0, 0.66069, 0, -0.74938_11.67 (0).png");

        var settings = new AppSettings { CullMode = CullMode.DeleteAfterRead };
        var results = Culler(settings).Apply(_folder, escape);

        Assert.Equal(CullRefusal.OutsideWatchedFolder, results.Single().Refusal);
        Assert.Empty(_deleted);
    }

    [Fact]
    public void DeleteAfterRead_RefusesAFileWhoseNameIsNotAScreenshot()
    {
        var notes = WriteOther("important-notes.png");

        var settings = new AppSettings { CullMode = CullMode.DeleteAfterRead };
        var results = Culler(settings).Apply(_folder, notes);

        Assert.Equal(CullRefusal.NotAScreenshotName, results.Single().Refusal);
        Assert.True(File.Exists(notes));
    }

    [Fact]
    public void AFailedDeleteIsReportedAndTheFileSurvives()
    {
        var shot = WriteShot(1);

        var settings = new AppSettings { CullMode = CullMode.DeleteAfterRead };
        var culler = new ScreenshotCuller(settings) { DeleteOverride = _ => false };

        var results = culler.Apply(_folder, shot);

        Assert.Equal(CullRefusal.DeleteFailed, results.Single().Refusal);
        Assert.False(results.Single().Deleted);
        Assert.True(File.Exists(shot));
    }

    [Fact]
    public void ALockedFileIsReportedRatherThanThrowing()
    {
        var shot = WriteShot(1);

        var settings = new AppSettings { CullMode = CullMode.DeleteAfterRead };
        var culler = new ScreenshotCuller(settings)
        {
            DeleteOverride = _ => throw new IOException("file in use by the game"),
        };

        var results = culler.Apply(_folder, shot);

        Assert.Equal(CullRefusal.DeleteFailed, results.Single().Refusal);
        Assert.True(File.Exists(shot));
    }

    // ---- Keep-latest behavior ---------------------------------------------

    [Fact]
    public void KeepLatest_KeepsExactlyTheNewestN()
    {
        for (var i = 0; i < 12; i++)
            WriteShot(i);

        var settings = new AppSettings { CullMode = CullMode.KeepLatest, CullKeepCount = 5 };
        var results = Culler(settings).Apply(_folder);

        Assert.Equal(7, results.Count);
        Assert.All(results, r => Assert.True(r.Deleted));

        var survivors = Directory.GetFiles(_folder).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(5, survivors.Length);

        // Minutes 07..11 are the five newest.
        Assert.All(survivors, name => Assert.Contains("19-0", name!.Replace("19-1", "19-0")));
        Assert.Contains(survivors, name => name!.Contains("[19-11]"));
        Assert.DoesNotContain(survivors, name => name!.Contains("[19-00]"));
    }

    [Fact]
    public void KeepLatest_DoesNothingWhenTheFolderIsAlreadyUnderTheLimit()
    {
        WriteShot(1);
        WriteShot(2);

        var settings = new AppSettings { CullMode = CullMode.KeepLatest, CullKeepCount = 20 };
        var results = Culler(settings).Apply(_folder);

        Assert.Empty(results);
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public void DeleteAfterRead_RemovesOnlyTheFileJustRead()
    {
        var keep = WriteShot(1);
        var read = WriteShot(2);

        var settings = new AppSettings { CullMode = CullMode.DeleteAfterRead };
        var results = Culler(settings).Apply(_folder, read);

        Assert.True(results.Single().Deleted);
        Assert.False(File.Exists(read));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void DeleteAfterRead_WithNoFileToReport_DoesNothing()
    {
        WriteShot(1);

        var settings = new AppSettings { CullMode = CullMode.DeleteAfterRead };

        Assert.Empty(Culler(settings).Apply(_folder));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void Apply_OnAMissingFolder_IsANoOpRatherThanAThrow()
    {
        var settings = new AppSettings { CullMode = CullMode.KeepLatest, CullKeepCount = 1 };

        Assert.Empty(Culler(settings).Apply(Path.Combine(_folder, "does-not-exist")));
    }

    // ---- Enumeration -------------------------------------------------------

    [Fact]
    public void EnumerateScreenshots_IgnoresSubfoldersAndForeignFiles()
    {
        WriteShot(1);
        WriteOther("readme.txt");

        var nested = Path.Combine(_folder, "archive");
        Directory.CreateDirectory(nested);
        File.WriteAllText(
            Path.Combine(nested, "2026-08-07[19-05]_1, 2, 3_0, 0, 0, 1_7.47.png"),
            "x");

        Assert.Single(ScreenshotCuller.EnumerateScreenshots(_folder));
    }

    // ---- Path containment --------------------------------------------------

    [Theory]
    [InlineData(@"C:\shots", @"C:\shots\a.png", true)]
    [InlineData(@"C:\shots\", @"C:\shots\a.png", true)]
    [InlineData(@"C:\SHOTS", @"C:\shots\a.png", true)]      // Windows paths are case-insensitive
    [InlineData(@"C:\shots", @"C:\shots\sub\a.png", false)]  // direct children only
    [InlineData(@"C:\shots", @"C:\other\a.png", false)]
    [InlineData(@"C:\shots", @"C:\shots\..\other\a.png", false)]
    [InlineData(@"C:\shots", @"C:\shotsx\a.png", false)]     // prefix match must not count
    [InlineData("", @"C:\shots\a.png", false)]
    [InlineData(@"C:\shots", "", false)]
    public void IsInsideFolder_OnlyAcceptsDirectChildrenOfTheResolvedFolder(
        string folder, string path, bool expected)
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Equal(expected, ScreenshotCuller.IsInsideFolder(folder, path));
    }
}
