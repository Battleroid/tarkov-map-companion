using System.Xml.Linq;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// Rewrites a tarkov.dev SVG map so only the requested floors are present.
/// </summary>
/// <remarks>
/// <para>
/// These maps stack every floor into one document as top-level <c>&lt;g id="..."&gt;</c> groups
/// (<c>Ground_Level</c>, <c>Second_Floor</c>, <c>Underground_Level</c>, ...). tarkov.dev shows and
/// hides them with CSS; we have no CSS engine in front of Skia, so we drop the unwanted groups
/// from the document before handing it to the rasterizer.
/// </para>
/// <para>
/// Two rules matter, both taken from tarkov.dev's own handling:
/// </para>
/// <list type="bullet">
///   <item><description>Only top-level <c>g</c> elements <em>with an id</em> are floors. Everything
///     else at the top level (<c>style</c>, <c>defs</c>) is shared and must always be kept, or the
///     map renders as untextured black shapes.</description></item>
///   <item><description>A group carrying <c>data-keep-with-group="&lt;base&gt;"</c> belongs to the
///     base floor and is drawn with it. Shoreline's <c>First_Floor</c> does this.</description></item>
/// </list>
/// </remarks>
public static class SvgLayerFilter
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    /// <summary>Ids of the top-level floor groups in document order.</summary>
    public static IReadOnlyList<string> ListLayerGroups(string svgText)
    {
        var root = Parse(svgText);
        return root is null ? [] : LayerGroups(root).Select(g => g.Attribute("id")!.Value).ToArray();
    }

    /// <summary>
    /// Returns the SVG with only the requested floor groups retained.
    /// </summary>
    /// <param name="svgText">The full map document.</param>
    /// <param name="baseLayerId">The ground-level group id, e.g. <c>Ground_Floor</c>.</param>
    /// <param name="extraLayerIds">Additional floor group ids to show.</param>
    /// <param name="includeBase">
    /// Whether to draw the ground level. Turning it off is the only way to actually see a level
    /// below it: these documents stack floors as opaque geometry, so Factory's Tunnels group
    /// renders underneath the ground floor and is invisible until the ground floor is dropped.
    /// </param>
    /// <remarks>
    /// If the document has no recognizable floor groups, or nothing was asked for that exists,
    /// the original text is returned untouched: showing the whole map beats showing nothing.
    /// Hiding the base deliberately is <em>not</em> that case, and is honored.
    /// </remarks>
    public static string Filter(
        string svgText,
        string? baseLayerId,
        IReadOnlyCollection<string> extraLayerIds,
        bool includeBase = true)
    {
        var root = Parse(svgText);
        if (root is null)
            return svgText;

        var groups = LayerGroups(root).ToArray();
        if (groups.Length == 0)
            return svgText;

        var keep = new HashSet<string>(StringComparer.Ordinal);

        if (includeBase && !string.IsNullOrEmpty(baseLayerId))
        {
            keep.Add(baseLayerId);

            foreach (var group in groups)
            {
                if (string.Equals(
                        group.Attribute("data-keep-with-group")?.Value,
                        baseLayerId,
                        StringComparison.Ordinal))
                {
                    keep.Add(group.Attribute("id")!.Value);
                }
            }
        }

        foreach (var id in extraLayerIds)
            keep.Add(id);

        var anyKept = groups.Any(g => keep.Contains(g.Attribute("id")!.Value));

        // Nothing recognized at all: fall back to the whole document rather than a blank map.
        // But if the caller deliberately dropped the base and asked for nothing else, an empty
        // map is the honest answer -- silently redrawing the ground floor would ignore them.
        if (!anyKept && includeBase)
            return svgText;

        foreach (var group in groups)
        {
            if (!keep.Contains(group.Attribute("id")!.Value))
                group.Remove();
        }

        return root.Document?.ToString(SaveOptions.DisableFormatting) ?? svgText;
    }

    private static IEnumerable<XElement> LayerGroups(XElement root) =>
        root.Elements()
            .Where(e => e.Name == Svg + "g" || e.Name.LocalName == "g")
            .Where(e => !string.IsNullOrEmpty(e.Attribute("id")?.Value));

    private static XElement? Parse(string svgText)
    {
        try
        {
            return XDocument.Parse(svgText, LoadOptions.PreserveWhitespace).Root;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}
