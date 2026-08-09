using Avalonia.Controls;
using Avalonia.Media;
using TarkovMapCompanion.Rendering;

namespace TarkovMapCompanion.Views;

/// <summary>
/// Seeds the color picker's swatch grid with the marker palette.
/// </summary>
/// <remarks>
/// <para>
/// The picker can reach any color, which is what was asked for. This just means the colors chosen
/// for the map are the ones sitting there when it opens: <see cref="MarkerPalette"/> separates its
/// hues in lightness as well as hue so they stay apart under deuteranopia and protanopia, and
/// nothing about a free picker preserves that. One click still gets a good answer.
/// </para>
/// <para>
/// The interface wants a rectangular grid, so the palette is laid out in rows of five rather than
/// as a flat list.
/// </para>
/// </remarks>
public sealed class MarkerColorPalette : IColorPalette
{
    private const int PerRow = 5;

    public int ColorCount => PerRow;

    public int ShadeCount => (MarkerPalette.PlayerChoices.Length + PerRow - 1) / PerRow;

    public Color GetColor(int colorIndex, int shadeIndex)
    {
        var index = (shadeIndex * PerRow) + colorIndex;

        // The last row can be short; repeating its final entry beats throwing inside a control.
        var choices = MarkerPalette.PlayerChoices;
        var color = choices[Math.Clamp(index, 0, choices.Length - 1)].Color;

        return Color.FromRgb(color.Red, color.Green, color.Blue);
    }
}
