using System.Globalization;

namespace TarkovMapCompanion.Maps;

/// <summary>Where a floor's imagery comes from.</summary>
public sealed record MapFloor(
    string Name,
    string? SvgLayerId,
    string? TilePathTemplate,
    bool ShownByDefault,
    IReadOnlyList<MapExtentJson> Extents)
{
    /// <summary>
    /// True when something at this height and horizontal position belongs on this floor.
    /// An extent with no footprints applies to the whole map at that height.
    /// </summary>
    public bool Covers(GamePosition position)
    {
        foreach (var extent in Extents)
        {
            var height = extent.Height;
            if (height is { Count: >= 2 } && (position.Y < height[0] || position.Y > height[1]))
                continue;

            if (extent.Bounds is not { Count: > 0 })
                return true;

            foreach (var footprint in extent.Bounds)
            {
                if (footprint.Contains(position.X, position.Z))
                    return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A map we can actually render: the interactive variant of a tarkov.dev map group, with its
/// projection resolved and its floors flattened into a usable list.
/// </summary>
public sealed class GameMap
{
    internal GameMap(MapGroupJson group, MapVariantJson variant)
    {
        NormalizedName = group.NormalizedName;
        DisplayName = ToDisplayName(group.NormalizedName);
        Description = group.Description;
        Key = variant.Key;

        Author = variant.Author;
        AuthorLink = variant.AuthorLink;

        Projection = new MapProjection(variant.CoordinateRotation ?? 0, variant.Transform);

        Bounds = variant.Bounds ?? throw new InvalidOperationException(
            $"map '{group.NormalizedName}' has no bounds; it cannot be projected");

        BaseRect = Projection.ToBaseRect(Bounds);

        if (UpstreamDataFixes.TryCorrectBounds(group.NormalizedName, Bounds, Projection, out var corrected))
        {
            BoundsWereCorrected = true;
            BaseRect = corrected;
        }

        // Only Reserve differs here, but getting it wrong silently misplaces the whole map.
        SvgBaseRect = variant.SvgBounds is { Count: >= 2 }
            ? Projection.ToBaseRect(variant.SvgBounds)
            : BaseRect;

        SvgUrl = variant.SvgPath;
        BaseSvgLayerId = variant.SvgLayer;
        BaseTilePathTemplate = variant.TilePath;

        // tarkov.dev's Leaflet layer defaults tileSize to 256 when the field is absent.
        TileSize = variant.TileSize ?? 256;
        MinZoomLevel = variant.MinZoom ?? 0;
        MaxZoomLevel = variant.MaxZoom ?? 6;

        HeightRange = variant.HeightRange is { Count: >= 2 }
            ? (variant.HeightRange[0], variant.HeightRange[1])
            : null;

        Floors = (variant.Layers ?? [])
            .Select(layer => new MapFloor(
                layer.Name,
                layer.SvgLayer,
                layer.TilePath,
                layer.Show,
                layer.Extents ?? []))
            .ToArray();

        Labels = variant.Labels?
            .Where(label => label.Position is { Count: >= 2 })
            .ToArray() ?? [];
    }

    public string NormalizedName { get; }
    public string DisplayName { get; }
    public string? Description { get; }
    public string Key { get; }

    public string? Author { get; }
    public string? AuthorLink { get; }

    public MapProjection Projection { get; }

    /// <summary>Game-space bounds as <c>[[x0, z0], [x1, z1]]</c>.</summary>
    public IReadOnlyList<IReadOnlyList<double>> Bounds { get; }

    /// <summary>Base-space rect the map covers. Tiles and marker culling use this.</summary>
    public MapRect BaseRect { get; }

    /// <summary>True when <see cref="BaseRect"/> came from a local fix rather than the source data.</summary>
    public bool BoundsWereCorrected { get; }

    /// <summary>Base-space rect the SVG overlay is stretched to. Equals <see cref="BaseRect"/> except on Reserve.</summary>
    public MapRect SvgBaseRect { get; }

    public string? SvgUrl { get; }
    public string? BaseSvgLayerId { get; }
    public string? BaseTilePathTemplate { get; }

    public int TileSize { get; }
    public int MinZoomLevel { get; }
    public int MaxZoomLevel { get; }

    /// <summary>Height band the base imagery represents, when the source declares one.</summary>
    public (double Min, double Max)? HeightRange { get; }

    public IReadOnlyList<MapFloor> Floors { get; }
    public IReadOnlyList<MapLabelJson> Labels { get; }

    public bool HasSvg => !string.IsNullOrWhiteSpace(SvgUrl);
    public bool HasTiles => !string.IsNullOrWhiteSpace(BaseTilePathTemplate);

    /// <summary>
    /// Whether a game position falls inside the map's extent.
    /// </summary>
    /// <remarks>
    /// Tested in base space rather than against the raw game-coordinate bounds, so that a map
    /// whose bounds needed correcting (see <see cref="UpstreamDataFixes"/>) is judged by the
    /// corrected extent. For every other map the two are equivalent.
    /// </remarks>
    public bool ContainsPosition(GamePosition position) => BaseRect.Contains(ToBase(position));

    public MapPoint ToBase(GamePosition position) => Projection.ToBase(position);

    public override string ToString() => DisplayName;

    /// <summary>
    /// "streets-of-tarkov" -> "Streets of Tarkov". The source JSON has no display name for every
    /// map, so derive one; the short words stay lowercase so it reads like a place name.
    /// </summary>
    internal static string ToDisplayName(string normalizedName)
    {
        if (string.IsNullOrWhiteSpace(normalizedName))
            return normalizedName;

        string[] lowercase = ["of", "the", "and"];
        var words = normalizedName.Split('-', StringSplitOptions.RemoveEmptyEntries);

        return string.Join(' ', words.Select((word, index) =>
            index > 0 && lowercase.Contains(word)
                ? word
                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(word)));
    }
}
