using TarkovMapCompanion.Screenshots;
using Xunit;

namespace TarkovMapCompanion.Tests;

public sealed class ScreenshotWatcherTests : IDisposable
{
    private readonly string _folder;

    public ScreenshotWatcherTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "tmc-watch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string WriteShot(int minute, double x = -720.10, double z = 430.51)
    {
        var name = $"2026-08-07[19-{minute:00}]_{x:F2}, -48.62, {z:F2}_0, 0.66069, 0, -0.74938_11.67 (0).png";
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "not really a png");
        return path;
    }

    [Fact]
    public void Start_DoesNotReplayFilesAlreadyInTheFolder()
    {
        WriteShot(1);
        WriteShot(2);

        var seen = new List<PlayerFix>();
        using var watcher = new ScreenshotWatcher();
        watcher.FixDetected += (_, fix) => seen.Add(fix);

        watcher.Start(_folder);

        // Starting mid-session must not dump an old raid onto the map.
        Assert.Empty(seen);
    }

    [Fact]
    public void Start_WithReportExisting_ReplaysThemOldestFirst()
    {
        WriteShot(3);
        WriteShot(1);
        WriteShot(2);

        var seen = new List<PlayerFix>();
        using var watcher = new ScreenshotWatcher();
        watcher.FixDetected += (_, fix) => seen.Add(fix);

        watcher.Start(_folder, reportExisting: true);

        Assert.Equal(3, seen.Count);
        Assert.Equal(seen.OrderBy(f => f.TakenAt).Select(f => f.FileName), seen.Select(f => f.FileName));
    }

    [Fact]
    public void Reconcile_PicksUpAFileTheLiveWatcherMissed()
    {
        // This is the whole reason the reconcile sweep exists: FileSystemWatcher drops events
        // under load, and a dropped event is a lost position fix.
        var seen = new List<PlayerFix>();
        using var watcher = new ScreenshotWatcher();
        watcher.FixDetected += (_, fix) => seen.Add(fix);

        watcher.Start(_folder);
        seen.Clear();

        var path = WriteShot(9);
        watcher.Reconcile();

        Assert.Single(seen);
        Assert.Equal(Path.GetFileName(path), seen[0].FileName);
    }

    [Fact]
    public void AFileIsReportedOnceEvenWhenBothPathsSeeIt()
    {
        var seen = new List<PlayerFix>();
        using var watcher = new ScreenshotWatcher();
        watcher.FixDetected += (_, fix) => seen.Add(fix);

        watcher.Start(_folder);
        WriteShot(9);

        watcher.Reconcile();
        watcher.Reconcile();
        watcher.Reconcile();

        Assert.Single(seen);
    }

    [Fact]
    public void Forget_LetsARecreatedFileBeReportedAgain()
    {
        var seen = new List<PlayerFix>();
        using var watcher = new ScreenshotWatcher();
        watcher.FixDetected += (_, fix) => { lock (seen) seen.Add(fix); };

        watcher.Start(_folder);

        var path = WriteShot(9);
        watcher.Reconcile();

        int CountFor(string p)
        {
            lock (seen) return seen.Count(f => string.Equals(f.FilePath, p, StringComparison.OrdinalIgnoreCase));
        }

        var before = CountFor(path);
        Assert.True(before >= 1, "the file should have been reported at least once");

        File.Delete(path);
        watcher.Forget(path);
        WriteShot(9);
        watcher.Reconcile();

        // Counted rather than compared against an exact total: a real FileSystemWatcher is running
        // alongside the deterministic Reconcile calls, and its events arrive on their own schedule.
        // What matters is that forgetting the path let it be reported again.
        Assert.True(
            CountFor(path) > before,
            $"expected the recreated file to be reported again; saw {CountFor(path)} reports, was {before}");
    }

    [Fact]
    public void Reconcile_IgnoresFilesThatAreNotScreenshots()
    {
        var seen = new List<PlayerFix>();
        using var watcher = new ScreenshotWatcher();
        watcher.FixDetected += (_, fix) => seen.Add(fix);

        watcher.Start(_folder);

        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_folder, "holiday.png"), "x");
        watcher.Reconcile();

        Assert.Empty(seen);
    }

    [Fact]
    public void Start_OnAMissingFolder_ReportsAnErrorInsteadOfThrowing()
    {
        var errors = new List<string>();
        using var watcher = new ScreenshotWatcher();
        watcher.Error += (_, message) => errors.Add(message);

        watcher.Start(Path.Combine(_folder, "nope"));

        Assert.Single(errors);
        Assert.False(watcher.IsWatching);
    }

    [Fact]
    public void Start_ReplacesThePreviousFolder()
    {
        var second = Path.Combine(_folder, "second");
        Directory.CreateDirectory(second);

        using var watcher = new ScreenshotWatcher();
        watcher.Start(_folder);
        watcher.Start(second);

        Assert.Equal(second, watcher.Folder);
    }

    [Fact]
    public void ReadFolder_ReturnsParsedFixesInCaptureOrder()
    {
        WriteShot(5);
        WriteShot(1);
        WriteShot(3);
        File.WriteAllText(Path.Combine(_folder, "junk.png"), "x");

        var fixes = ScreenshotWatcher.ReadFolder(_folder).ToArray();

        Assert.Equal(3, fixes.Length);
        Assert.Equal([1, 3, 5], fixes.Select(f => f.TakenAt.Minute));
    }

    [Fact]
    public void DisposeIsIdempotentAndStopsWatching()
    {
        var watcher = new ScreenshotWatcher();
        watcher.Start(_folder);

        watcher.Dispose();
        watcher.Dispose();

        Assert.False(watcher.IsWatching);
    }
}
