using TarkovMapCompanion.Maps;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Pins the coordinate maths against values derived from tarkov.dev's own Leaflet setup. If these
/// break, every marker on every map is in the wrong place, so they are deliberately concrete.
/// </summary>
public sealed class MapProjectionTests
{
    // Shoreline: transform [0.16, 83.2, 0.16, 111.1], rotation 180,
    // bounds [[504, -415], [-1056, 618]].
    private static readonly double[] ShorelineTransform = [0.16, 83.2, 0.16, 111.1];
    private static readonly List<List<double>> ShorelineBounds = [[504, -415], [-1056, 618]];

    private static MapProjection Shoreline() => new(180, ShorelineTransform);

    [Fact]
    public void ToBase_MatchesTheLeafletAffineByHand()
    {
        // rx = -x, ry = -z at 180 degrees.
        //   px =  0.16 * 720.10  + 83.2  = 198.416
        //   py = -0.16 * -430.51 + 111.1 = 179.9816
        var point = Shoreline().ToBase(-720.10, 430.51);

        Assert.Equal(198.416, point.X, 6);
        Assert.Equal(179.9816, point.Y, 6);
    }

    [Fact]
    public void ToBase_PlacesAKnownShorelineFixAtTheExpectedFractionOfTheMap()
    {
        // A real fix from Casey's screenshot folder, hand-checked during planning:
        //   across: (720.10 + 504) / 1560 = 0.7847
        //   down:   (415 + 430.51) / 1033 = 0.8185
        var projection = Shoreline();
        var rect = projection.ToBaseRect(ShorelineBounds);
        var point = projection.ToBase(-720.10, 430.51);

        Assert.Equal(0.7847, (point.X - rect.Left) / rect.Width, 4);
        Assert.Equal(0.8185, (point.Y - rect.Top) / rect.Height, 4);
    }

    [Fact]
    public void ToBaseRect_CoversExactlyTheGameSpaceExtentTimesTheScale()
    {
        // Shoreline spans 1560 x 1033 game meters at 0.16 base pixels per meter.
        var rect = Shoreline().ToBaseRect(ShorelineBounds);

        Assert.Equal(1560 * 0.16, rect.Width, 6);
        Assert.Equal(1033 * 0.16, rect.Height, 6);
    }

    [Fact]
    public void ToBaseRect_MapsTheBoundsCornersOntoTheRectCorners()
    {
        var projection = Shoreline();
        var rect = projection.ToBaseRect(ShorelineBounds);

        var first = projection.ToBase(504, -415);
        var second = projection.ToBase(-1056, 618);

        // At 180 degrees the first bounds corner becomes top-left and the second bottom-right.
        Assert.Equal(rect.Left, first.X, 6);
        Assert.Equal(rect.Top, first.Y, 6);
        Assert.Equal(rect.Right, second.X, 6);
        Assert.Equal(rect.Bottom, second.Y, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void ToGame_InvertsToBase(double rotation)
    {
        var projection = new MapProjection(rotation, [0.239, 168.65, 0.239, 136.35]);

        foreach (var (x, z) in new[] { (0.0, 0.0), (-720.10, 430.51), (325.5, -812.25), (1.0, -1.0) })
        {
            var (backX, backZ) = projection.ToGame(projection.ToBase(x, z));

            Assert.Equal(x, backX, 6);
            Assert.Equal(z, backZ, 6);
        }
    }

    [Fact]
    public void ToGame_InvertsToBase_ForAnAnisotropicTransform()
    {
        // Icebreaker is the one map whose x and y scales differ.
        var projection = new MapProjection(180, [2.0, 125.0, 3.5, 91.0]);
        var (x, z) = projection.ToGame(projection.ToBase(31.5, -12.25));

        Assert.Equal(31.5, x, 6);
        Assert.Equal(-12.25, z, 6);
    }

    [Fact]
    public void MissingTransform_IsTreatedAsIdentity()
    {
        var projection = new MapProjection(0, null);
        var point = projection.ToBase(12, 34);

        Assert.Equal(12, point.X, 6);
        Assert.Equal(-34, point.Y, 6);
    }

    [Fact]
    public void ZeroScaleTransform_IsRejectedRatherThanProducingInfinities()
    {
        Assert.Throws<ArgumentException>(() => new MapProjection(180, [0, 10, 0.5, 20]));
    }

    // ---- Heading ----------------------------------------------------------

    [Theory]
    // tarkov.dev: rotation = yaw + coordinateRotation, plus 180 more when rotation is 90 or 270.
    [InlineData(180, 0, 180)]
    [InlineData(180, 90, 270)]
    [InlineData(180, 172.03, 352.03)]
    [InlineData(180, 202.76, 22.76)]
    [InlineData(0, 45, 45)]
    [InlineData(90, 0, 270)]
    [InlineData(90, 90, 0)]
    [InlineData(270, 0, 90)]
    [InlineData(270, 90, 180)]
    public void ScreenAngleDegrees_MatchesTarkovDevsMarkerRotation(double rotation, double yaw, double expected)
    {
        var angle = new MapProjection(rotation, [1, 0, 1, 0]).ScreenAngleDegrees(yaw);

        Assert.Equal(expected, angle, 6);
    }

    [Fact]
    public void ScreenAngleDegrees_AgreesWithTheDirectionTheMarkerActuallyMoves()
    {
        // Independent check: project two points a meter apart along a known heading and confirm
        // the reported screen angle points the same way in base space.
        foreach (double rotation in new double[] { 0, 90, 180, 270 })
        {
            var projection = new MapProjection(rotation, [0.4, 17.0, 0.4, -9.0]);

            foreach (double yaw in new double[] { 0, 30, 120, 200, 300 })
            {
                var yawRad = yaw * Math.PI / 180.0;
                var from = new GamePosition(50, 0, -20);
                var to = new GamePosition(from.X + Math.Sin(yawRad), 0, from.Z + Math.Cos(yawRad));

                var a = projection.ToBase(from);
                var b = projection.ToBase(to);

                // Clockwise from screen-up, which is -Y in base space.
                var moved = MapProjection.Normalize360(
                    Math.Atan2(b.X - a.X, -(b.Y - a.Y)) * 180.0 / Math.PI);

                Assert.Equal(projection.ScreenAngleDegrees(yaw), moved, 4);
            }
        }
    }

    // ---- Bearings and angle helpers ---------------------------------------

    [Theory]
    [InlineData(0, 1, 0)]      // due +Z
    [InlineData(1, 0, 90)]     // due +X
    [InlineData(0, -1, 180)]
    [InlineData(-1, 0, 270)]
    public void BearingDegrees_UsesTheSameClockwiseFromPlusZConventionAsYaw(double dx, double dz, double expected)
    {
        var from = new GamePosition(10, 5, -3);
        var to = new GamePosition(10 + dx, 5, -3 + dz);

        Assert.Equal(expected, MapProjection.BearingDegrees(from, to), 6);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(360, 0)]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    public void Normalize360_WrapsIntoRange(double given, double expected) =>
        Assert.Equal(expected, MapProjection.Normalize360(given), 6);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(180, 180)]
    [InlineData(181, -179)]
    [InlineData(-190, 170)]
    public void NormalizeSigned_WrapsIntoHalfOpenRange(double given, double expected) =>
        Assert.Equal(expected, MapProjection.NormalizeSigned(given), 6);

    [Fact]
    public void GroundDistance_IgnoresHeight()
    {
        var a = new GamePosition(0, -58, 0);
        var b = new GamePosition(3, 120, 4);

        Assert.Equal(5, a.GroundDistanceTo(b), 6);
    }
}
