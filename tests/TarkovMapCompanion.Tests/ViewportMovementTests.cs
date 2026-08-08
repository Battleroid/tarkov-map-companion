using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Rendering;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Working out where the view is going without going there.
/// </summary>
/// <remarks>
/// Smooth camera movement needs its destination up front so it can ease towards it. If a computed
/// destination did not match where a jump would have landed, the animation would drift somewhere
/// slightly wrong every time it ran, which is far harder to notice than an outright break.
/// </remarks>
public sealed class ViewportMovementTests
{
    private static Viewport Sized()
    {
        var viewport = new Viewport(new MapRect(0, 0, 1000, 800));
        viewport.Resize(600, 400);
        return viewport;
    }

    [Fact]
    public void TheComputedViewMatchesActuallyFittingTheRect()
    {
        var target = new MapRect(200, 100, 600, 500);

        var predicted = Sized().StateForRect(target, 0.15);

        var actual = Sized();
        actual.FitToRect(target, 0.15);

        Assert.Equal(predicted.Scale, actual.Scale, 10);
        Assert.Equal(predicted.Center.X, actual.Center.X, 10);
        Assert.Equal(predicted.Center.Y, actual.Center.Y, 10);
    }

    [Fact]
    public void ComputingAViewDoesNotMoveTheCurrentOne()
    {
        var viewport = Sized();
        var before = viewport.Capture();

        viewport.StateForRect(new MapRect(0, 0, 10, 10));
        viewport.StateForCenter(new MapPoint(999, 999));

        Assert.Equal(before.Scale, viewport.Capture().Scale);
        Assert.Equal(before.Center.X, viewport.Capture().Center.X);
    }

    [Fact]
    public void CenteringKeepsTheCurrentZoom()
    {
        var viewport = Sized();
        viewport.Scale = 2.5;

        Assert.Equal(2.5, viewport.StateForCenter(new MapPoint(400, 300)).Scale);
    }

    [Fact]
    public void TheComputedCentreIsClampedTheSameWayAJumpWouldBe()
    {
        // The animation's destination has to be the place a jump would have reached, or the ease
        // finishes somewhere the viewport then quietly corrects.
        var predicted = Sized().StateForCenter(new MapPoint(100_000, 100_000));

        var actual = Sized();
        actual.Center = new MapPoint(100_000, 100_000);

        Assert.Equal(actual.Center.X, predicted.Center.X, 10);
        Assert.Equal(actual.Center.Y, predicted.Center.Y, 10);
    }

    [Fact]
    public void ADegenerateRectDoesNotSlamIntoMaximumZoom()
    {
        var point = new MapRect(500, 400, 500, 400);

        var state = Sized().StateForRect(point);

        Assert.True(state.Scale <= Sized().MaxScale);
        Assert.True(double.IsFinite(state.Scale));
    }
}
