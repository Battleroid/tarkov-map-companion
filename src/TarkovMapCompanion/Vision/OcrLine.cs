namespace TarkovMapCompanion.Vision;

/// <summary>Axis-aligned box in image pixels.</summary>
public readonly record struct TextBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterY => Y + (Height / 2.0);

    public TextBox Union(TextBox other)
    {
        var left = Math.Min(X, other.X);
        var top = Math.Min(Y, other.Y);

        return new TextBox(left, top, Math.Max(Right, other.Right) - left, Math.Max(Bottom, other.Bottom) - top);
    }

    public TextBox Offset(double dx, double dy) => new(X + dx, Y + dy, Width, Height);
}

/// <summary>One run of text the reader found, with where it sat in the image.</summary>
/// <remarks>
/// The geometry matters as much as the text. The extraction panel is a table, and the only way to
/// tell which name belongs to which row is that they share a vertical band.
/// </remarks>
public sealed record OcrLine(string Text, TextBox Bounds);

/// <summary>
/// A sub-rectangle of an image given as fractions of its size.
/// </summary>
/// <remarks>
/// Fractions rather than pixels because the same region has to work for whatever resolution the
/// game is running at. Casey plays at 2560x1440; the panel is anchored to the top-right corner and
/// grows downward, so a fraction of the frame tracks it where a pixel box would not.
/// </remarks>
public readonly record struct RelativeRegion(double Left, double Top, double Right, double Bottom)
{
    public static readonly RelativeRegion Whole = new(0.0, 0.0, 1.0, 1.0);

    /// <summary>
    /// Where Tarkov puts the extraction list. Deliberately generous: the panel's left edge moves
    /// with the longest exit name, and its bottom edge with the number of rows, neither of which
    /// we know before reading it.
    /// </summary>
    /// <remarks>
    /// Cropping is not only about speed. Running the reader over the whole frame picks up graffiti,
    /// weapon labels and the hotbar, and every one of those is a string that could fuzzy-match an
    /// exit name by accident. A tight region is the cheapest false-positive defense available.
    /// </remarks>
    public static readonly RelativeRegion ExtractPanel = new(0.45, 0.0, 1.0, 0.75);

    /// <summary>
    /// Converts to whole pixels inside an image of the given size, clamped so a malformed region
    /// cannot ask the decoder for pixels that are not there.
    /// </summary>
    public (int X, int Y, int Width, int Height) ToPixels(int imageWidth, int imageHeight)
    {
        var x0 = (int)Math.Floor(Math.Clamp(Left, 0.0, 1.0) * imageWidth);
        var y0 = (int)Math.Floor(Math.Clamp(Top, 0.0, 1.0) * imageHeight);
        var x1 = (int)Math.Ceiling(Math.Clamp(Right, 0.0, 1.0) * imageWidth);
        var y1 = (int)Math.Ceiling(Math.Clamp(Bottom, 0.0, 1.0) * imageHeight);

        // At least one pixel each way; a zero-sized bitmap throws inside the decoder.
        var width = Math.Clamp(x1 - x0, 1, imageWidth);
        var height = Math.Clamp(y1 - y0, 1, imageHeight);

        return (Math.Min(x0, imageWidth - width), Math.Min(y0, imageHeight - height), width, height);
    }
}
