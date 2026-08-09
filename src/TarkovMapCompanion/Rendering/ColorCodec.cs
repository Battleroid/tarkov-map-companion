using System.Globalization;
using SkiaSharp;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// Turns a color into something a settings file or a peer can carry, and back.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately forgiving on the way in and canonical on the way out. Both of its callers hand it
/// text nobody validated: the settings file is documented as hand-editable, and a peer's color
/// arrives over a socket from a program we did not write. Neither is allowed to throw somewhere
/// deep in a draw call, so there is no parse path here that does.
/// </para>
/// <para>
/// Alpha is accepted on input but never emitted. A marker is a thing you are meant to see, and a
/// color is not the place to make one disappear -- overlays already control their own transparency
/// for staleness and floor dimming, and a stored alpha would silently fight them.
/// </para>
/// </remarks>
public static class ColorCodec
{
    /// <summary>Reads <c>#RGB</c>, <c>#RRGGBB</c> or <c>#AARRGGBB</c>, with or without the hash.</summary>
    public static bool TryParse(string? text, out SKColor color)
    {
        color = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hex = text.Trim().TrimStart('#');

        // Shorthand: #F5C expands to #FF55CC, the same way CSS does it.
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);

        if (hex.Length is not (6 or 8))
            return false;

        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return false;

        // The 8-digit form is AARRGGBB, so the alpha is already in the top byte; the 6-digit form
        // has to have one supplied. Either way it is discarded below -- see the remarks.
        var rgb = hex.Length == 8 ? value : value | 0xFF000000u;

        color = new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    /// <summary>Reads a color, falling back rather than failing.</summary>
    public static SKColor Parse(string? text, SKColor fallback) =>
        TryParse(text, out var color) ? color : fallback.WithAlpha(0xFF);

    /// <summary>Always <c>#RRGGBB</c>, upper case, so a round trip through the settings file is stable.</summary>
    public static string ToHex(SKColor color) =>
        $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    /// <summary>
    /// What <c>Normalize()</c> stores: a value that reads back identically, or the default.
    /// </summary>
    public static string Canonical(string? text, SKColor fallback) =>
        ToHex(Parse(text, fallback));
}
