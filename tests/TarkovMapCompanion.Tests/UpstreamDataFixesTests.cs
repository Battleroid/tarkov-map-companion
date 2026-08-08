using TarkovMapCompanion.Maps;
using Xunit;

namespace TarkovMapCompanion.Tests;

public sealed class UpstreamDataFixesTests
{
    private static readonly MapCatalog Catalog = MapCatalog.LoadEmbedded();

    [Fact]
    public void Icebreaker_GetsItsCopyPastedFactoryBoundsReplaced()
    {
        var icebreaker = Catalog.Find("icebreaker")!;

        Assert.True(icebreaker.BoundsWereCorrected);
        Assert.Equal(256, icebreaker.BaseRect.Width, 6);
        Assert.Equal(256, icebreaker.BaseRect.Height, 6);
    }

    [Fact]
    public void NoOtherMapIsCorrected()
    {
        var corrected = Catalog.Maps.Where(m => m.BoundsWereCorrected).Select(m => m.NormalizedName).ToArray();

        Assert.Equal(["icebreaker"], corrected);
    }

    [Fact]
    public void Factory_KeepsItsOwnBounds_EvenThoughTheValuesAreTheOnesWeMatchOn()
    {
        // The fix keys on the map name as well as the value, so Factory must be untouched.
        var factory = Catalog.Find("factory")!;

        Assert.False(factory.BoundsWereCorrected);
    }

    [Fact]
    public void TheFixDisablesItselfOnceUpstreamChangesTheValue()
    {
        // Same map, plausible corrected bounds: our override must step aside.
        var catalog = MapCatalog.Parse("""
            [
              { "normalizedName": "icebreaker",
                "maps": [ { "key": "icebreaker", "projection": "interactive",
                            "coordinateRotation": 180, "transform": [2.0, 125.0, 3.5, 91.0],
                            "bounds": [[62.5, -26.0], [-65.5, 47.14]],
                            "tilePath": "https://example.invalid/{z}/{x}/{y}.png" } ] }
            ]
            """);

        var icebreaker = catalog.Find("icebreaker")!;

        Assert.False(icebreaker.BoundsWereCorrected);
    }

    [Fact]
    public void ContainsPosition_UsesTheCorrectedExtent()
    {
        var icebreaker = Catalog.Find("icebreaker")!;

        // Base (0,0) and (256,256) are the corrected canvas corners; invert them to game space
        // and confirm both ends register as inside.
        var (nearX, nearZ) = icebreaker.Projection.ToGame(new MapPoint(1, 1));
        var (farX, farZ) = icebreaker.Projection.ToGame(new MapPoint(255, 255));

        Assert.True(icebreaker.ContainsPosition(new GamePosition(nearX, 19, nearZ)));
        Assert.True(icebreaker.ContainsPosition(new GamePosition(farX, 19, farZ)));

        // A point well outside the canvas must not register.
        var (outX, outZ) = icebreaker.Projection.ToGame(new MapPoint(-400, -400));
        Assert.False(icebreaker.ContainsPosition(new GamePosition(outX, 19, outZ)));
    }

    [Fact]
    public void ProjectionIsUntouchedByTheBoundsFix()
    {
        // Only framing and containment change; marker placement comes from transform + rotation.
        var icebreaker = Catalog.Find("icebreaker")!;

        Assert.Equal(2.0, icebreaker.Projection.ScaleX, 6);
        Assert.Equal(3.5, icebreaker.Projection.ScaleY, 6);
        Assert.Equal(180, icebreaker.Projection.CoordinateRotationDegrees);
    }
}
