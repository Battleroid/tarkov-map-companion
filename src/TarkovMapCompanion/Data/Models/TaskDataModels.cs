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

    /// <summary>
    /// Keys this task needs, grouped by the map they open something on.
    /// </summary>
    /// <remarks>
    /// Upstream already scopes these per map, which is what makes "what do I need on Customs"
    /// answerable at all. A key is the one requirement that cannot be improvised once you are in
    /// the raid.
    /// </remarks>
    [JsonPropertyName("keys")] public List<TaskKeyData> Keys { get; set; } = [];

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

    /// <summary>
    /// The items this objective wants.
    /// </summary>
    /// <remarks>
    /// Only when the list is short enough to be a list. One objective upstream names 3,493 items,
    /// which is a category rather than a shopping list, and its own description already says so.
    /// The cut is at <see cref="MaxNamedItems"/>: measured against real data, 562 of the 580
    /// objectives that reference items name twelve or fewer, and every one that names more is a
    /// "hand over any of these" of some kind.
    /// </remarks>
    [JsonPropertyName("items")] public List<TaskItemData> Items { get; set; } = [];

    /// <summary>Beyond this many, an item list is a category rather than a list.</summary>
    public const int MaxNamedItems = 12;
}

/// <summary>Keys needed on one map.</summary>
public sealed class TaskKeyData
{
    /// <summary>BSG map id, matching the one on a point.</summary>
    [JsonPropertyName("map")] public string MapId { get; set; } = "";

    /// <summary>The keys themselves.</summary>
    [JsonPropertyName("keys")] public List<TaskItemData> Keys { get; set; } = [];
}

/// <summary>
/// One item, by name and by BSG id.
/// </summary>
/// <remarks>
/// The id is carried because it is the whole address of the item's picture:
/// <c>assets.tarkov.dev/{id}-icon.webp</c>. Storing it costs 25 bytes against a name already
/// there, and the 366 items the snapshot keeps come to 9 KB of ids — cheaper than any other way
/// of getting from "Dorm room 220 key" to a picture of it.
/// </remarks>
public sealed class TaskItemData
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>Already resolved to English.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    public override string ToString() => Name;
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
