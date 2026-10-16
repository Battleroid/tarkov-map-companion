using SkiaSharp;
using TarkovMapCompanion.Localization;
using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// The typefaces the map overlays draw text with.
/// </summary>
/// <remarks>
/// <para>
/// Centralised because the same chain was copy-pasted into every overlay. Neither <c>Cascadia
/// Mono</c> nor <c>Consolas</c> carries CJK glyphs, so Chinese drawn on the map would come out as
/// tofu boxes; a CJK-capable family is therefore part of the chain.
/// </para>
/// <para>
/// When the active language is Chinese the CJK-capable families are tried first, because
/// <see cref="SKTypeface.FromFamilyName"/> picks a single family and does not fall back per glyph.
/// Prefixing the chain the other way would find Cascadia Mono on any modern Windows and never reach
/// the font that can draw the text. The first family that resolves for the language is used.
/// </para>
/// </remarks>
public static class AppFonts
{
    private static readonly string[] MonoFamilies =
    [
        "Cascadia Mono",
        "Consolas",
    ];

    /// <summary>
    /// CJK-capable families, most-preferred first. <c>Microsoft YaHei UI</c> ships with every
    /// Windows 10/11 install; the rest are fallbacks for trimmed or regional images.
    /// </summary>
    private static readonly string[] CjkFamilies =
    [
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "微软雅黑",
        "SimSun",
        "Noto Sans CJK SC",
    ];

    /// <summary>The typeface for on-canvas labels, honouring the active language.</summary>
    public static SKTypeface Label => Build(Localizer.Language);

    private static SKTypeface Build(AppLanguage language)
    {
        var families = language == AppLanguage.ChineseSimplified
            ? new[] { CjkFamilies, MonoFamilies }
            : new[] { MonoFamilies, CjkFamilies };

        foreach (var group in families)
        {
            foreach (var family in group)
            {
                if (SKTypeface.FromFamilyName(family) is { } typeface)
                    return typeface;
            }
        }

        return SKTypeface.Default;
    }
}
