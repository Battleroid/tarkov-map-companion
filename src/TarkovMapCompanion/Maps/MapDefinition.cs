using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovMapCompanion.Maps;

/// <summary>
/// One entry of tarkov.dev's <c>src/data/maps.json</c>. A map can carry several renderings (2D
/// stills, 3D views, the interactive one); we only ever use the interactive entry.
/// </summary>
public sealed class MapGroupJson
{
    /// <summary>Quoted for some maps and a bare number for others, hence the lenient converter.</summary>
    [JsonPropertyName("id")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Id { get; set; }
    [JsonPropertyName("normalizedName")] public string NormalizedName { get; set; } = "";
    [JsonPropertyName("primaryPath")] public string? PrimaryPath { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("maps")] public List<MapVariantJson> Maps { get; set; } = [];
}

public sealed class MapVariantJson
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("projection")] public string? Projection { get; set; }

    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("authorLink")] public string? AuthorLink { get; set; }

    [JsonPropertyName("minZoom")] public int? MinZoom { get; set; }
    [JsonPropertyName("maxZoom")] public int? MaxZoom { get; set; }
    [JsonPropertyName("tileSize")] public int? TileSize { get; set; }

    [JsonPropertyName("transform")] public List<double>? Transform { get; set; }
    [JsonPropertyName("coordinateRotation")] public double? CoordinateRotation { get; set; }

    [JsonPropertyName("bounds")] public List<List<double>>? Bounds { get; set; }

    /// <summary>
    /// Where the SVG overlay goes, when it differs from <see cref="Bounds"/>. Only Reserve sets
    /// this, and ignoring it puts every Reserve marker about 20 game meters out.
    /// </summary>
    [JsonPropertyName("svgBounds")] public List<List<double>>? SvgBounds { get; set; }

    [JsonPropertyName("heightRange")] public List<double>? HeightRange { get; set; }

    [JsonPropertyName("svgPath")] public string? SvgPath { get; set; }

    /// <summary>Id of the SVG group holding the ground floor, e.g. <c>Ground_Level</c>.</summary>
    [JsonPropertyName("svgLayer")] public string? SvgLayer { get; set; }

    [JsonPropertyName("tilePath")] public string? TilePath { get; set; }

    [JsonPropertyName("layers")] public List<MapLayerJson>? Layers { get; set; }
    [JsonPropertyName("labels")] public List<MapLabelJson>? Labels { get; set; }
}

/// <summary>An additional floor: a different SVG group, a different tile pyramid, or both.</summary>
public sealed class MapLayerJson
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("svgLayer")] public string? SvgLayer { get; set; }
    [JsonPropertyName("tilePath")] public string? TilePath { get; set; }
    [JsonPropertyName("show")] public bool Show { get; set; }
    [JsonPropertyName("extents")] public List<MapExtentJson>? Extents { get; set; }
}

/// <summary>
/// The volume a layer covers: a height band, optionally restricted to some footprints. A marker
/// belongs to the layer when its height is inside <see cref="Height"/> and, if footprints are
/// given, it also falls inside one of them.
/// </summary>
public sealed class MapExtentJson
{
    [JsonPropertyName("height")] public List<double>? Height { get; set; }

    [JsonPropertyName("bounds")]
    [JsonConverter(typeof(ExtentBoundsConverter))]
    public List<ExtentFootprint>? Bounds { get; set; }
}

/// <summary>
/// One footprint from a layer extent. In JSON these are <c>[[x1,z1],[x2,z2]]</c> with an optional
/// third element naming the area, which is why they need a custom converter.
/// </summary>
public sealed record ExtentFootprint(double X1, double Z1, double X2, double Z2, string? Name)
{
    public bool Contains(double x, double z) =>
        x >= Math.Min(X1, X2) && x <= Math.Max(X1, X2) &&
        z >= Math.Min(Z1, Z2) && z <= Math.Max(Z1, Z2);
}

/// <remarks>
/// The numeric fields go through <see cref="LenientDoubleConverter"/> because this data is
/// hand-maintained upstream and does not keep its types straight: at least one Customs label
/// carries <c>"rotation": "6"</c> as a string, which is enough to fail the whole catalog parse.
/// </remarks>
public sealed class MapLabelJson
{
    /// <summary>Game <c>[x, z]</c>.</summary>
    [JsonPropertyName("position")] public List<double>? Position { get; set; }

    [JsonPropertyName("text")] public string Text { get; set; } = "";

    [JsonPropertyName("top")]
    [JsonConverter(typeof(LenientDoubleConverter))]
    public double? Top { get; set; }

    [JsonPropertyName("bottom")]
    [JsonConverter(typeof(LenientDoubleConverter))]
    public double? Bottom { get; set; }

    [JsonPropertyName("size")]
    [JsonConverter(typeof(LenientDoubleConverter))]
    public double? Size { get; set; }

    [JsonPropertyName("rotation")]
    [JsonConverter(typeof(LenientDoubleConverter))]
    public double? Rotation { get; set; }
}

/// <summary>Reads a scalar of any type as a string. Upstream quotes some ids and not others.</summary>
internal sealed class LenientStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => null,
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}

/// <summary>
/// Reads a number that may have been written as a JSON string. Anything unparseable becomes null
/// rather than an exception, since these fields are all cosmetic.
/// </summary>
internal sealed class LenientDoubleConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.String => double.TryParse(
                reader.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) ? parsed : null,
            _ => null,
        };

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteNumberValue(value.Value);
        else
            writer.WriteNullValue();
    }
}

/// <summary>
/// Reads the heterogeneous <c>[[x1,z1],[x2,z2],"label"]</c> shape that layer extent bounds use.
/// Malformed entries are skipped rather than thrown on: a bad footprint should cost one floor
/// hint, not the whole map catalog.
/// </summary>
internal sealed class ExtentBoundsConverter : JsonConverter<List<ExtentFootprint>>
{
    public override List<ExtentFootprint>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        var result = new List<ExtentFootprint>();

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array)
                continue;

            var parts = entry.EnumerateArray().ToArray();
            if (parts.Length < 2)
                continue;

            if (!TryReadPair(parts[0], out var x1, out var z1) || !TryReadPair(parts[1], out var x2, out var z2))
                continue;

            var name = parts.Length > 2 && parts[2].ValueKind == JsonValueKind.String
                ? parts[2].GetString()
                : null;

            result.Add(new ExtentFootprint(x1, z1, x2, z2, name));
        }

        return result;
    }

    private static bool TryReadPair(JsonElement element, out double first, out double second)
    {
        first = second = 0;

        if (element.ValueKind != JsonValueKind.Array)
            return false;

        var numbers = element.EnumerateArray().ToArray();
        if (numbers.Length < 2 ||
            numbers[0].ValueKind != JsonValueKind.Number ||
            numbers[1].ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        first = numbers[0].GetDouble();
        second = numbers[1].GetDouble();
        return true;
    }

    public override void Write(Utf8JsonWriter writer, List<ExtentFootprint> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var footprint in value)
        {
            writer.WriteStartArray();
            writer.WriteStartArray();
            writer.WriteNumberValue(footprint.X1);
            writer.WriteNumberValue(footprint.Z1);
            writer.WriteEndArray();
            writer.WriteStartArray();
            writer.WriteNumberValue(footprint.X2);
            writer.WriteNumberValue(footprint.Z2);
            writer.WriteEndArray();
            if (footprint.Name is not null)
                writer.WriteStringValue(footprint.Name);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }
}
