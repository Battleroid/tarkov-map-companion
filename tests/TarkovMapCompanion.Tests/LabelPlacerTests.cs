using SkiaSharp;
using TarkovMapCompanion.Rendering;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Keeping names off each other, which no single overlay can do on its own.
/// </summary>
public sealed class LabelPlacerTests
{
    private static readonly SKPaint Text = new() { TextSize = 12 };

    private static LabelPlacer Frame(float width = 800, float height = 600)
    {
        var placer = new LabelPlacer();
        placer.BeginFrame(new SKRect(0, 0, width, height));

        return placer;
    }

    /// <summary>The first label to ask gets exactly where it asked for.</summary>
    [Fact]
    public void AnUncontestedLabelDoesNotMove()
    {
        var placer = Frame();

        var spot = placer.Place(100, 100, 10, "Old Gas Station", Text);

        Assert.NotNull(spot);
        Assert.Equal(110, spot!.Value.X, 1);
        Assert.False(spot.Value.NeedsLeader);
    }

    [Fact]
    public void TheSecondLabelAtTheSameSpotMovesOffIt()
    {
        var placer = Frame();

        var first = placer.Place(100, 100, 10, "Old Gas Station", Text)!.Value;
        var second = placer.Place(100, 100, 10, "Scav Checkpoint", Text)!.Value;

        Assert.NotEqual(first.Y, second.Y);
    }

    /// <summary>
    /// Moved far enough that "the nearest marker" stops being the answer, so it says which.
    /// </summary>
    [Fact]
    public void ALabelPushedFarGetsALeader()
    {
        var placer = Frame();

        // Four labels stacked on one point: the first two are near enough to read as theirs, and
        // by the third the ladder has walked past the threshold.
        var spots = Enumerable.Range(0, 4)
            .Select(i => placer.Place(100, 100, 10, $"Exit {i}", Text))
            .ToArray();

        Assert.All(spots, s => Assert.NotNull(s));
        Assert.Contains(spots, s => s!.Value.NeedsLeader);
    }

    /// <summary>A label never runs off the edge of the map view.</summary>
    [Fact]
    public void LabelsStayInsideTheFrame()
    {
        var placer = Frame(200, 200);

        var spot = placer.Place(195, 100, 10, "Railroad to Military Base", Text);

        if (spot is { } placed)
        {
            Assert.True(placed.X >= 0, $"x {placed.X}");
            Assert.True(placed.X + Text.MeasureText("Railroad to Military Base") <= 200);
        }
    }

    /// <summary>
    /// When there is genuinely nowhere left, the label is dropped rather than piled on.
    /// </summary>
    /// <remarks>
    /// The marker is still drawn and hovering it still names it. Four names printed through each
    /// other leaves nobody able to read any of them, which is worse than three and a gap.
    /// </remarks>
    [Fact]
    public void AHopelessLabelIsSkipped()
    {
        var placer = Frame(120, 40);

        Assert.NotNull(placer.Place(10, 20, 4, "Dorms", Text));

        var crowd = Enumerable.Range(0, 12)
            .Select(_ => placer.Place(10, 20, 4, "Dorms", Text))
            .ToArray();

        Assert.Contains(crowd, s => s is null);
    }

    /// <summary>Turning it off puts every label exactly where its overlay asked.</summary>
    [Fact]
    public void DisabledMeansNoMovementAtAll()
    {
        var placer = Frame();
        placer.IsEnabled = false;

        var first = placer.Place(100, 100, 10, "Old Gas Station", Text)!.Value;
        var second = placer.Place(100, 100, 10, "Scav Checkpoint", Text)!.Value;

        Assert.Equal(first.X, second.X, 1);
        Assert.Equal(first.Y, second.Y, 1);
        Assert.False(second.NeedsLeader);
    }

    [Fact]
    public void BeginFrameForgetsTheLastOne()
    {
        var placer = Frame();

        var first = placer.Place(100, 100, 10, "Old Gas Station", Text)!.Value;

        placer.BeginFrame(new SKRect(0, 0, 800, 600));

        var again = placer.Place(100, 100, 10, "Old Gas Station", Text)!.Value;

        Assert.Equal(first.X, again.X, 1);
        Assert.Equal(first.Y, again.Y, 1);
    }

    /// <summary>A blocked area is respected by the labels that come after it.</summary>
    [Fact]
    public void BlockedAreasAreAvoided()
    {
        var placer = Frame();

        placer.Block(new SKRect(105, 80, 400, 120));

        var spot = placer.Place(100, 100, 10, "Old Gas Station", Text)!.Value;

        Assert.True(spot.Y < 80 || spot.Y > 132, $"landed at y {spot.Y} inside the blocked band");
    }

    /// <summary>A note sits above its dot, and only ever moves vertically.</summary>
    [Fact]
    public void NotesKeepTheirColumn()
    {
        var placer = Frame();

        var first = placer.PlaceAbove(100, 100, 7, "Big Red", Text)!.Value;
        var second = placer.PlaceAbove(100, 100, 7, "the sniper shack", Text)!.Value;

        Assert.Equal(100, first.X, 1);
        Assert.Equal(100, second.X, 1);
        Assert.NotEqual(first.Y, second.Y);
    }
}
