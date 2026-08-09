using SkiaSharp;
using TarkovMapCompanion.Rendering;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// The stopping rule, which is the only part of the clock worth testing: one that never stops
/// wastes a battery for hours, and one that stops too eagerly freezes a pulse partway through with
/// nothing on screen to explain it. The timer itself needs a dispatcher and is left alone.
/// </summary>
public class MapClockTests
{
    private sealed class Source : IAnimatedOverlay
    {
        public int ZOrder => 0;
        public bool IsVisible { get; set; } = true;
        public bool Moving { get; set; }
        public int AdvanceCalls { get; private set; }

        public bool Advance()
        {
            AdvanceCalls++;
            return Moving;
        }

        public void Draw(SKCanvas canvas, Viewport viewport)
        {
        }
    }

    [Fact]
    public void NothingMovingStopsTheClock()
    {
        var sources = new[] { new Source(), new Source() };

        Assert.False(MapClock.ShouldKeepRunning(sources));
    }

    [Fact]
    public void OneMovingSourceKeepsItRunning()
    {
        var sources = new[] { new Source(), new Source { Moving = true } };

        Assert.True(MapClock.ShouldKeepRunning(sources));
    }

    [Fact]
    public void AHiddenOverlayDoesNotKeepItRunning()
    {
        var sources = new[] { new Source { Moving = true, IsVisible = false } };

        Assert.False(MapClock.ShouldKeepRunning(sources));
    }

    /// <summary>
    /// Advance is how an overlay retires expired state, so short-circuiting the moment one source
    /// asks for more frames would leave every later source holding onto things that should be gone.
    /// </summary>
    [Fact]
    public void EverySourceIsAdvancedEvenAfterOneAsksForMoreFrames()
    {
        var first = new Source { Moving = true };
        var second = new Source();

        MapClock.ShouldKeepRunning([first, second]);

        Assert.Equal(1, first.AdvanceCalls);
        Assert.Equal(1, second.AdvanceCalls);
    }

    /// <summary>
    /// A hidden source is still advanced. Pings expire on wall time whether or not anyone is
    /// looking, and coming back to a map holding a pile of long-dead ones would be worse.
    /// </summary>
    [Fact]
    public void AHiddenSourceIsStillAdvanced()
    {
        var hidden = new Source { Moving = true, IsVisible = false };

        MapClock.ShouldKeepRunning([hidden]);

        Assert.Equal(1, hidden.AdvanceCalls);
    }

    [Fact]
    public void NoSourcesAtAllIsNotAReasonToRun()
    {
        Assert.False(MapClock.ShouldKeepRunning([]));
    }
}
