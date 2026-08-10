using System.Text.Json.Serialization;

namespace TarkovMapCompanion.Data;

/// <summary>
/// A piece of text somebody put on the map.
/// </summary>
/// <remarks>
/// <para>
/// Held in game coordinates, like everything else that is a place rather than a pixel. Base pixel
/// space belongs to one map at one projection, so storing that would make a file unusable the
/// moment the artwork was re-registered, and unusable to anybody who imported it.
/// </para>
/// <para>
/// The id is what makes editing, deleting and sharing work without matching on text: two people can
/// both label a building "Red Rebel stash" without either of them overwriting the other.
/// </para>
/// </remarks>
public sealed class MapAnnotation
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Normalized map name, e.g. <c>customs</c>.</summary>
    [JsonPropertyName("map")] public string Map { get; set; } = "";

    [JsonPropertyName("x")] public double X { get; set; }

    [JsonPropertyName("z")] public double Z { get; set; }

    [JsonPropertyName("text")] public string Text { get; set; } = "";

    /// <summary>
    /// Who wrote it, when it came from the squad. Null for your own.
    /// </summary>
    /// <remarks>
    /// Present so a shared note is attributable and, more usefully, so yours can be told from
    /// theirs when deciding what you are allowed to delete.
    /// </remarks>
    [JsonPropertyName("author")] public string? Author { get; set; }

    /// <summary>Longest a label may be, so one cannot be pasted across the whole map.</summary>
    public const int MaxTextLength = 60;

    /// <summary>Trims and clips text to something drawable, returning null when nothing is left.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Newlines and tabs would break the single-line drawing and the CSV round trip alike.
        var flattened = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();

        while (flattened.Contains("  ", StringComparison.Ordinal))
            flattened = flattened.Replace("  ", " ", StringComparison.Ordinal);

        if (flattened.Length == 0)
            return null;

        return flattened.Length > MaxTextLength ? flattened[..MaxTextLength] : flattened;
    }
}

/// <summary>The file format, so a shared file says which app and version wrote it.</summary>
public sealed class AnnotationFile
{
    [JsonPropertyName("app")] public string App { get; set; } = "TarkovMapCompanion";

    [JsonPropertyName("version")] public int Version { get; set; } = 1;

    [JsonPropertyName("annotations")] public List<MapAnnotation> Annotations { get; set; } = [];
}
