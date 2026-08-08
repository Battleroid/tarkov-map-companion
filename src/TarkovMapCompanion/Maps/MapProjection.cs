namespace TarkovMapCompanion.Maps;

/// <summary>
/// Converts Tarkov world coordinates into a map's base pixel space, and back.
/// </summary>
/// <remarks>
/// <para>
/// This reproduces exactly what tarkov.dev's Leaflet setup does, because the map imagery, the POI
/// coordinates and the <c>bounds</c> rectangles all come from tarkov.dev and only line up if we
/// place them the same way. Their <c>getCRS</c> builds a <c>L.CRS.Simple</c> with:
/// </para>
/// <code>
///   projection:     rotate the LatLng by coordinateRotation, then project as Point(lng, lat)
///   transformation: L.Transformation(transform[0], transform[1], -transform[2], transform[3])
/// </code>
/// <para>
/// Positions are fed in as <c>LatLng(z, x)</c>, so lng is the game X and lat is the game Z. Working
/// that through gives the affine below. Note the sign flip on the Y scale: Leaflet's transformation
/// negates it so that pixel Y grows downward.
/// </para>
/// <code>
///   rx = x*cos(r) - z*sin(r)
///   ry = x*sin(r) + z*cos(r)
///   px =  a*rx + b
///   py = -c*ry + d          where transform = [a, b, c, d]
/// </code>
/// <para>
/// "Base pixel space" here is that CRS pixel space at zoom 0. Leaflet multiplies it by 2^zoom;
/// we let <c>Viewport</c> apply an arbitrary scale instead, so zoom is continuous rather than
/// stepped. Everything drawn on the map -- imagery, markers, heatmap -- is positioned in this
/// space, which is what keeps them mutually consistent.
/// </para>
/// </remarks>
public sealed class MapProjection
{
    private readonly double _cos;
    private readonly double _sin;
    private readonly double _a;
    private readonly double _b;
    private readonly double _c;
    private readonly double _d;

    public MapProjection(double coordinateRotationDegrees, IReadOnlyList<double>? transform)
    {
        CoordinateRotationDegrees = coordinateRotationDegrees;

        var radians = coordinateRotationDegrees * Math.PI / 180.0;
        _cos = Math.Cos(radians);
        _sin = Math.Sin(radians);

        // Leaflet treats a missing transform as the identity.
        if (transform is { Count: >= 4 })
        {
            _a = transform[0];
            _b = transform[1];
            _c = transform[2];
            _d = transform[3];
        }
        else
        {
            _a = 1;
            _b = 0;
            _c = 1;
            _d = 0;
        }

        if (_a == 0 || _c == 0)
            throw new ArgumentException("transform scale components must be non-zero", nameof(transform));
    }

    public double CoordinateRotationDegrees { get; }

    /// <summary>
    /// Base pixels per game meter along each axis. Separate values because Icebreaker's transform
    /// is genuinely anisotropic (2.0 vs 3.5); every other map has a == c.
    /// </summary>
    public double ScaleX => Math.Abs(_a);

    /// <inheritdoc cref="ScaleX"/>
    public double ScaleY => Math.Abs(_c);

    /// <summary>Single scalar for things that only need an approximate meters-to-pixels factor.</summary>
    public double AverageScale => (ScaleX + ScaleY) / 2.0;

    public MapPoint ToBase(GamePosition position) => ToBase(position.X, position.Z);

    public MapPoint ToBase(double gameX, double gameZ)
    {
        var rx = gameX * _cos - gameZ * _sin;
        var ry = gameX * _sin + gameZ * _cos;
        return new MapPoint(_a * rx + _b, -_c * ry + _d);
    }

    /// <summary>Inverse of <see cref="ToBase(double, double)"/>. Returns game (x, z); height is not recoverable.</summary>
    public (double X, double Z) ToGame(MapPoint point)
    {
        var rx = (point.X - _b) / _a;
        var ry = (_d - point.Y) / _c;

        // Undo the rotation: the forward rotation matrix is orthonormal, so its inverse is its transpose.
        var x = rx * _cos + ry * _sin;
        var z = -rx * _sin + ry * _cos;
        return (x, z);
    }

    /// <summary>
    /// Base-space rectangle for a tarkov.dev bounds pair, given as <c>[[x0, z0], [x1, z1]]</c> in
    /// game coordinates. Used to place map imagery, which is stretched to exactly this rect.
    /// </summary>
    public MapRect ToBaseRect(IReadOnlyList<IReadOnlyList<double>> bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        if (bounds.Count < 2 || bounds[0].Count < 2 || bounds[1].Count < 2)
            throw new ArgumentException("bounds must be [[x0, z0], [x1, z1]]", nameof(bounds));

        // Rotation can swap which corner ends up top-left, so normalize rather than assume.
        return MapRect.FromCorners(
            ToBase(bounds[0][0], bounds[0][1]),
            ToBase(bounds[1][0], bounds[1][1]));
    }

    /// <summary>
    /// Clockwise screen rotation, in degrees, for a marker whose artwork points up at 0.
    /// </summary>
    /// <param name="yawDegrees">
    /// Game yaw, clockwise from +Z, as produced by <c>ScreenshotNameParser</c>.
    /// </param>
    /// <remarks>
    /// Derived from the affine above: a heading (sin y, cos y) maps to the base-space direction
    /// (a*(sin y*cos r - cos y*sin r), -c*(sin y*sin r + cos y*cos r)), whose clockwise angle from
    /// screen-up works out to yaw + r, plus a further 180 degrees when r is 90 or 270. That extra
    /// half turn matches what tarkov.dev applies to its own player marker.
    /// </remarks>
    public double ScreenAngleDegrees(double yawDegrees)
    {
        var angle = yawDegrees + CoordinateRotationDegrees;

        if (Math.Abs(CoordinateRotationDegrees % 180.0) > 1e-9)
            angle += 180.0;

        return Normalize360(angle);
    }

    /// <summary>Wraps an angle into [0, 360).</summary>
    public static double Normalize360(double degrees)
    {
        var wrapped = degrees % 360.0;
        return wrapped < 0 ? wrapped + 360.0 : wrapped;
    }

    /// <summary>Wraps an angle into (-180, 180]. Useful for "how far off is my heading" readouts.</summary>
    public static double NormalizeSigned(double degrees)
    {
        var wrapped = Normalize360(degrees);
        return wrapped > 180.0 ? wrapped - 360.0 : wrapped;
    }

    /// <summary>
    /// Compass-style bearing in degrees from <paramref name="from"/> to <paramref name="to"/>,
    /// measured clockwise from +Z, i.e. the same convention as the yaw out of a screenshot name.
    /// </summary>
    public static double BearingDegrees(GamePosition from, GamePosition to)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        return Normalize360(Math.Atan2(dx, dz) * 180.0 / Math.PI);
    }
}
