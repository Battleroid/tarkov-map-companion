namespace TarkovMapCompanion.Maps;

/// <summary>A point in the map's base pixel space (the CRS pixel space at zoom 0).</summary>
public readonly record struct MapPoint(double X, double Y)
{
    public static MapPoint operator +(MapPoint a, MapPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static MapPoint operator -(MapPoint a, MapPoint b) => new(a.X - b.X, a.Y - b.Y);

    public double DistanceTo(MapPoint other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>An axis-aligned rectangle in base pixel space. Always normalized (Left &lt;= Right, Top &lt;= Bottom).</summary>
public readonly record struct MapRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public MapPoint Center => new((Left + Right) / 2, (Top + Bottom) / 2);

    public static MapRect FromCorners(MapPoint a, MapPoint b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    /// <summary>Grows the rect by <paramref name="fraction"/> of its own size on every side.</summary>
    public MapRect Inflate(double fraction)
    {
        var dx = Width * fraction;
        var dy = Height * fraction;
        return new MapRect(Left - dx, Top - dy, Right + dx, Bottom + dy);
    }

    /// <summary>Grows the rect by an absolute amount on every side.</summary>
    public MapRect InflateBy(double amount) => new(Left - amount, Top - amount, Right + amount, Bottom + amount);

    public bool Contains(MapPoint p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;
}

/// <summary>A position in Tarkov's world space, straight out of a screenshot filename.</summary>
public readonly record struct GamePosition(double X, double Y, double Z)
{
    /// <summary>Horizontal distance in meters, ignoring height.</summary>
    public double GroundDistanceTo(GamePosition other)
    {
        var dx = X - other.X;
        var dz = Z - other.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }
}
