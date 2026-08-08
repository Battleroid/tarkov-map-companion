using TarkovMapCompanion.Screenshots;
using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Exercises the real deletion path, not a stub.
/// </summary>
/// <remarks>
/// Every other culler test replaces deletion with a fake so it does not touch the Recycle Bin.
/// That left the one piece of genuinely dangerous code in the project -- the only place the app
/// destroys user data -- with no test executing it at all, and a marshalling bug in it crashed
/// the process on the first screenshot that triggered a cull. These tests run it for real, on
/// files in the temp directory.
/// </remarks>
public sealed class RecycleBinTests : IDisposable
{
    private readonly string _folder;

    public RecycleBinTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "tmc-recycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string WriteFile(string name = "probe.txt")
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "recycle bin test");
        return path;
    }

    [Fact]
    public void TryDelete_ActuallyRemovesTheFile()
    {
        if (!RecycleBin.IsSupported)
            return;

        var path = WriteFile();

        Assert.True(RecycleBin.TryDelete(path), "the shell refused to recycle the file");
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryDelete_HandlesAPathWithSpacesAndPunctuation()
    {
        // Tarkov's own filenames are full of spaces, commas, brackets and parentheses, so the
        // marshalling has to survive them.
        if (!RecycleBin.IsSupported)
            return;

        var path = WriteFile("2026-08-07[19-31]_-720.10, -48.62, 430.51_0, 0.66, 0, -0.74_11.67 (0).png");

        Assert.True(RecycleBin.TryDelete(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryDelete_ReturnsFalseForAMissingFileRatherThanThrowing()
    {
        if (!RecycleBin.IsSupported)
            return;

        Assert.False(RecycleBin.TryDelete(Path.Combine(_folder, "never-existed.txt")));
    }

    [Fact]
    public void TryDelete_SurvivesBeingCalledRepeatedly()
    {
        // The crash showed up on the second call in a batch, so a single call proving nothing is
        // not enough: culling deletes many files in a row.
        if (!RecycleBin.IsSupported)
            return;

        for (var i = 0; i < 12; i++)
        {
            var path = WriteFile($"batch-{i}.txt");

            Assert.True(RecycleBin.TryDelete(path), $"failed on file {i}");
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public void TheCullerReallyDeletesWhenNotStubbed()
    {
        // End to end through the culler with no DeleteOverride, which is the exact path that ran
        // in the app when it crashed: keep the newest N and recycle the rest.
        if (!RecycleBin.IsSupported)
            return;

        for (var minute = 0; minute < 12; minute++)
        {
            var name = $"2026-08-07[19-{minute:00}]_-720.10, -48.62, 430.51_0, 0.66, 0, -0.74_11.67 (0).png";
            var path = Path.Combine(_folder, name);
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 7, 19, minute, 0, DateTimeKind.Utc));
        }

        var settings = new AppSettings
        {
            CullMode = CullMode.KeepLatest,
            CullKeepCount = 5,
            CullToRecycleBin = true,
        };

        var results = new ScreenshotCuller(settings).Apply(_folder);

        Assert.Equal(7, results.Count);
        Assert.All(results, r => Assert.True(r.Deleted, $"{Path.GetFileName(r.Path)}: {r.Refusal}"));
        Assert.Equal(5, ScreenshotCuller.EnumerateScreenshots(_folder).Count);
    }
}
