using System.Text.Json.Serialization;

namespace TarkovMapCompanion.Data.Models;

/// <summary>
/// The bundled task snapshot: every quest, with the objectives that have a place on a map.
/// </summary>
/// <remarks>
/// A projection of <c>json.tarkov.dev/regular/tasks</c> rather than the payload itself. Upstream is
/// 2.2 MB and carries the whole reward graph, item lists and image links; what is kept here is what
/// can be drawn or filtered on, which comes to 92 KB gzipped for all 510 tasks.
/// </remarks>
public sealed class TaskDocument
{
    [JsonPropertyName("tasks")]
    public List<TaskData> Tasks { get; set; } = [];

    [JsonPropertyName("fetchedAt")]
    public DateTimeOffset? FetchedAt { get; set; }
}

public sealed class TaskData
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>Already resolved to English. The upstream field is a localization key.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("normalizedName")] public string NormalizedName { get; set; } = "";

    /// <summary>Trader's display name, e.g. <c>Prapor</c>. Resolved when the snapshot is built.</summary>
    [JsonPropertyName("trader")] public string Trader { get; set; } = "";

    [JsonPropertyName("minPlayerLevel")] public int MinPlayerLevel { get; set; }

    [JsonPropertyName("kappaRequired")] public bool KappaRequired { get; set; }

    [JsonPropertyName("lightkeeperRequired")] public bool LightkeeperRequired { get; set; }

    /// <summary><c>BEAR</c> or <c>USEC</c> when the task is faction-locked, otherwise null.</summary>
    [JsonPropertyName("faction")] public string? Faction { get; set; }

    [JsonPropertyName("wikiLink")] public string? WikiLink { get; set; }

    /// <summary>BSG map id, when the whole task belongs to one map. Often absent.</summary>
    [JsonPropertyName("map")] public string? MapId { get; set; }

    /// <summary>Ids of tasks that have to be finished first.</summary>
    [JsonPropertyName("requires")] public List<string> Requires { get; set; } = [];

    [JsonPropertyName("objectives")] public List<TaskObjectiveData> Objectives { get; set; } = [];
}

public sealed class TaskObjectiveData
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>
    /// One of <c>visit</c>, <c>mark</c>, <c>plantItem</c>, <c>shoot</c>, <c>giveItem</c> and a
    /// dozen more. Kept as a string: the set grows with the game and an unknown value should read
    /// oddly, not throw.
    /// </summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "";

    /// <summary>
    /// The objective as the game words it, e.g. "Hand over any found in raid medicine items".
    /// </summary>
    /// <remarks>
    /// This is why the 16 MB item payload is not needed: the description already names the items.
    /// </remarks>
    [JsonPropertyName("description")] public string Description { get; set; } = "";

    [JsonPropertyName("optional")] public bool Optional { get; set; }

    [JsonPropertyName("count")] public int? Count { get; set; }

    [JsonPropertyName("foundInRaid")] public bool FoundInRaid { get; set; }

    /// <summary>Where on a map this happens. Empty for the many objectives that are not anywhere.</summary>
    [JsonPropertyName("points")] public List<TaskPointData> Points { get; set; } = [];
}

/// <summary>Somewhere an objective happens, in game coordinates.</summary>
public sealed class TaskPointData
{
    /// <summary>BSG map id, the same key the POI data uses.</summary>
    [JsonPropertyName("map")] public string MapId { get; set; } = "";

    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("z")] public double Z { get; set; }

    /// <summary>
    /// The zone's footprint as <c>[x, z]</c> pairs, when it has one.
    /// </summary>
    /// <remarks>
    /// Kept because a "visit" objective is an area rather than a dot, and drawing the area is the
    /// difference between "somewhere near here" and "stand in this room".
    /// </remarks>
    [JsonPropertyName("outline")] public List<double[]>? Outline { get; set; }

    /// <summary>
    /// True when this is one of several places the thing might be, rather than where it is.
    /// </summary>
    /// <remarks>
    /// Quest items spawn in a few possible spots. Saying so is the honest rendering; a confident
    /// marker on each would have you walk past four of them.
    /// </remarks>
    [JsonPropertyName("oneOf")] public bool OneOf { get; set; }

    /// <summary>The footprint as coordinate pairs, skipping anything malformed.</summary>
    [JsonIgnore]
    public IEnumerable<(double X, double Z)> OutlinePoints =>
        Outline?.Where(p => p.Length >= 2).Select(p => (p[0], p[1])) ?? [];
}
