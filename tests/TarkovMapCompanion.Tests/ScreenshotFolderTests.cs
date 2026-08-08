using TarkovMapCompanion.Screenshots;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Finding where Tarkov is actually writing.
/// </summary>
/// <remarks>
/// The failure this prevents is the quietest one in the app: watch the wrong folder and everything
/// looks healthy while the map simply never moves. It cost a squad member an evening of not
/// appearing on anyone's map.
/// </remarks>
public sealed class ScreenshotFolderTests : IDisposable
{
    private readonly string _root;

    public ScreenshotFolderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tmc-folders", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    /// <summary>Creates a candidate root, optionally with screenshots inside it.</summary>
    private string Root(string name, int screenshots = 0, bool exists = true)
    {
        var root = Path.Combine(_root, name);

        if (!exists)
            return root;

        var folder = Path.Combine(root, "Escape from Tarkov", "Screenshots");
        Directory.CreateDirectory(folder);

        for (var i = 0; i < screenshots; i++)
        {
            var file = $"2026-08-08[14-{i:00}]_569.17, 2.90, -54.63_0, 0.77, 0, 0.63_13.83 (0).png";
            File.WriteAllText(Path.Combine(folder, file), "x");
        }

        return root;
    }

    [Fact]
    public void TheFolderWithScreenshotsWinsEvenWhenItIsListedLast()
    {
        // The shell's idea of Documents is checked first, but if OneDrive has the actual files then
        // OneDrive is the answer. Files on disk beat any reasoning about shell folders.
        var empty = Root("documents");
        var real = Root("onedrive", screenshots: 4);

        var best = ScreenshotFolders.Evaluate([(empty, "shell Documents"), (real, "OneDrive")]).First();

        Assert.StartsWith(real, best.Path, StringComparison.Ordinal);
        Assert.Equal("OneDrive", best.Source);
        Assert.Equal(4, best.ScreenshotCount);
        Assert.True(best.Looks);
    }

    [Fact]
    public void AFolderThatExistsBeatsOneThatDoesNot()
    {
        var missing = Root("gone", exists: false);
        var empty = Root("documents");

        var best = ScreenshotFolders.Evaluate([(missing, "shell Documents"), (empty, "OneDrive")]).First();

        Assert.True(best.Exists);
        Assert.False(best.Looks);
    }

    [Fact]
    public void TheSamePathListedTwiceIsOnlyReportedOnce()
    {
        // The shell path and the profile path are usually the same folder; showing it twice would
        // make the diagnostic output read as though there were two places to check.
        var root = Root("documents", screenshots: 2);

        var found = ScreenshotFolders.Evaluate([(root, "shell Documents"), (root, "profile Documents")]);

        Assert.Single(found);
    }

    [Fact]
    public void NullAndNonsenseRootsAreSkippedRatherThanThrowing()
    {
        var real = Root("documents", screenshots: 1);

        var found = ScreenshotFolders.Evaluate(
        [
            (null, "OneDrive"),
            ("", "OneDrive for Business"),
            (real, "shell Documents"),
        ]);

        Assert.Single(found);
        Assert.True(found[0].Looks);
    }

    [Fact]
    public void OnlyTarkovsOwnFilenamesAreCounted()
    {
        var root = Root("documents", screenshots: 3);
        var folder = Path.Combine(root, "Escape from Tarkov", "Screenshots");

        File.WriteAllText(Path.Combine(folder, "holiday.png"), "x");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "x");

        Assert.Equal(3, ScreenshotFolders.CountIn(folder));
    }

    [Fact]
    public void CountingAMissingFolderIsZeroRatherThanAnError()
    {
        Assert.Equal(0, ScreenshotFolders.CountIn(Path.Combine(_root, "nope")));
        Assert.Equal(0, ScreenshotFolders.CountIn(""));
        Assert.Equal(0, ScreenshotFolders.CountIn(null));
    }

    [Fact]
    public void DetectAlwaysReturnsSomethingUsable()
    {
        // Even on a machine where the game has never run, the default has to point somewhere
        // sensible rather than at an empty string.
        var detected = ScreenshotFolders.Detect();

        Assert.False(string.IsNullOrWhiteSpace(detected));
        Assert.EndsWith(Path.Combine("Escape from Tarkov", "Screenshots"), detected, StringComparison.Ordinal);
    }
}
