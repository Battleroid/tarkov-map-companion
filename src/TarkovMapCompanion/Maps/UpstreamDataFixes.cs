namespace TarkovMapCompanion.Maps;

/// <summary>
/// Corrections for known defects in tarkov.dev's map data.
/// </summary>
/// <remarks>
/// Every fix here is written to <em>disable itself</em> once upstream corrects the data: each one
/// first checks that the exact broken value is still present. That way refreshing the catalog
/// picks up the real fix instead of being silently overridden by a stale guess of ours.
/// </remarks>
internal static class UpstreamDataFixes
{
    /// <summary>
    /// Factory's bounds, which were copy-pasted onto Icebreaker.
    /// </summary>
    private static readonly double[][] FactoryBounds = [[77, -64.5], [-65.5, 67.4]];

    /// <summary>
    /// Every tarkov.dev tile pyramid is generated onto a square canvas of this many base units,
    /// with the map letterboxed inside. Verified against Customs, whose base rect spans
    /// x 1.83..257.56 -- essentially the full canvas -- and against Icebreaker's pyramid, which
    /// serves a complete 16x16 grid of tiles at zoom 4.
    /// </summary>
    private const double TileCanvasSize = 256.0;

    /// <summary>
    /// Replaces bounds we know to be wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Icebreaker ships Factory's bounds verbatim. The consequences are real rather than cosmetic:
    /// "fit the whole map" frames a mostly empty rectangle roughly twice the size of the ship, and
    /// the containment test used to suggest a map accepts positions nowhere near it.
    /// </para>
    /// <para>
    /// The replacement is the tile canvas, which is guaranteed to contain the imagery without
    /// having to guess at a tighter rectangle. Note that the <em>projection</em> is unaffected --
    /// marker positions come from <c>transform</c> and <c>coordinateRotation</c>, which are correct
    /// for Icebreaker, so this only changes framing and containment.
    /// </para>
    /// </remarks>
    public static bool TryCorrectBounds(
        string normalizedName,
        IReadOnlyList<IReadOnlyList<double>> declared,
        MapProjection projection,
        out MapRect corrected)
    {
        corrected = default;

        if (!string.Equals(normalizedName, "icebreaker", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!MatchesFactoryBounds(declared))
            return false;

        corrected = new MapRect(0, 0, TileCanvasSize, TileCanvasSize);
        return true;
    }

    private static bool MatchesFactoryBounds(IReadOnlyList<IReadOnlyList<double>> declared)
    {
        if (declared.Count != FactoryBounds.Length)
            return false;

        for (var i = 0; i < declared.Count; i++)
        {
            if (declared[i].Count != FactoryBounds[i].Length)
                return false;

            for (var j = 0; j < declared[i].Count; j++)
            {
                if (Math.Abs(declared[i][j] - FactoryBounds[i][j]) > 1e-9)
                    return false;
            }
        }

        return true;
    }
}
