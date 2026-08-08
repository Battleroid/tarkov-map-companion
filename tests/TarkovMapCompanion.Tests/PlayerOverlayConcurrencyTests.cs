using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Screenshots;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// The player trail is written by the folder-watcher thread and read by the UI and render threads.
/// Before it was guarded, a screenshot arriving while a frame was being drawn could throw on a
/// thread with no handler, which closes a windowed app instantly and silently.
/// </summary>
public sealed class PlayerOverlayConcurrencyTests
{
    private static PlayerFix Fix(int minute, double raidHours) => new()
    {
        Position = new GamePosition(minute, -50, minute * 2),
        YawDegrees = 0,
        Rotation = (0, 0, 0, 1),
        RaidTimeHours = raidHours,
        TakenAt = new DateTime(2026, 8, 7, 19, Math.Clamp(minute, 0, 59), 0),
        FilePath = $@"C:\s\{minute}.png",
    };

    [Fact]
    public void AddingFixesWhileReadingDoesNotThrow()
    {
        var overlay = new PlayerOverlay { TrailLength = 12 };
        var failures = new List<Exception>();
        var stop = false;

        // Writer: the folder watcher. Alternates between continuing a raid and starting a new one,
        // so the clear-and-rebuild path is exercised too.
        var writer = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < 4000; i++)
                {
                    var newRaid = i % 50 == 0;
                    overlay.Add(Fix(i % 60, newRaid ? 4 + i * 0.001 : 10 + i * 0.0001));
                }
            }
            catch (Exception ex)
            {
                lock (failures) failures.Add(ex);
            }
            finally
            {
                Volatile.Write(ref stop, true);
            }
        });

        // Readers: the UI thread reading the status bar, and the render thread walking the trail.
        Thread Reader(Action read) => new(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                    read();
            }
            catch (Exception ex)
            {
                lock (failures) failures.Add(ex);
            }
        });

        var elapsedReader = Reader(() => _ = overlay.RaidElapsed);
        var historyReader = Reader(() =>
        {
            // Exactly what the renderer does: walk the trail and touch every entry.
            foreach (var fix in overlay.History)
                _ = fix.Position.X;
        });

        writer.Start();
        elapsedReader.Start();
        historyReader.Start();

        Assert.True(writer.Join(TimeSpan.FromSeconds(30)), "writer did not finish");
        elapsedReader.Join(TimeSpan.FromSeconds(5));
        historyReader.Join(TimeSpan.FromSeconds(5));

        Assert.True(
            failures.Count == 0,
            $"concurrent access threw: {string.Join("; ", failures.Select(f => f.GetType().Name + ": " + f.Message))}");
    }

    [Fact]
    public void HistoryHandsOutACopy_SoACallerCannotSeeItMutate()
    {
        var overlay = new PlayerOverlay();
        overlay.Add(Fix(1, 10.0));

        var snapshot = overlay.History;
        overlay.Add(Fix(2, 10.01));

        Assert.Single(snapshot);
        Assert.Equal(2, overlay.History.Count);
    }

    [Fact]
    public void RaidElapsedIsConsistentWithTheTrailItReportsOn()
    {
        var overlay = new PlayerOverlay();

        Assert.Equal(TimeSpan.Zero, overlay.RaidElapsed);

        overlay.Add(Fix(1, 10.33));
        Assert.Equal(TimeSpan.Zero, overlay.RaidElapsed);

        overlay.Add(Fix(2, 10.37));
        Assert.True(overlay.RaidElapsed > TimeSpan.Zero);

        // A fix from a different raid resets both the trail and the elapsed clock together.
        overlay.Add(Fix(3, 4.20));
        Assert.Equal(TimeSpan.Zero, overlay.RaidElapsed);
        Assert.Single(overlay.History);
    }
}
