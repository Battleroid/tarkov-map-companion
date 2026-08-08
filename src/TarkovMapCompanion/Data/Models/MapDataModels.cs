using System.Text.Json.Serialization;

namespace TarkovMapCompanion.Data.Models;

/// <summary>
/// Point-of-interest data for every map, as served by <c>json.tarkov.dev</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the payload tarkov.dev's own site consumes at runtime. It is <em>not</em> the GraphQL
/// schema: the site pre-bakes these JSON blobs and never calls GraphQL from the browser. Worth
/// knowing, because GraphQL is the documented API and was returning
/// <c>422 GraphQL server unavailable</c> throughout development while this endpoint stayed up.
/// </para>
/// <para>
/// The shape is normalized for transport, so almost nothing is nested:
/// </para>
/// <list type="bullet">
///   <item><description>Maps, mobs, loot containers and stationary weapons are objects keyed by
///     id; references between them are bare id strings.</description></item>
///   <item><description>Display names are localization keys (<c>EXFIL_ZB013</c>,
///     <c>578f87a3245977356274f2cb Name</c>) resolved through a separate translations
///     document.</description></item>
/// </list>
/// </remarks>
public sealed class MapDataDocument
{
    [JsonPropertyName("maps")]
    public Dictionary<string, MapPoiData> Maps { get; set; } = [];

    /// <summary>Bosses and other named AI, keyed by mob id such as <c>bossBully</c>.</summary>
    [JsonPropertyName("mobs")]
    public Dictionary<string, NamedIdData> Mobs { get; set; } = [];

    [JsonPropertyName("lootContainers")]
    public Dictionary<string, NamedIdData> LootContainers { get; set; } = [];

    [JsonPropertyName("stationaryWeapons")]
    public Dictionary<string, NamedIdData> StationaryWeapons { get; set; } = [];

    /// <summary>Localization-key to English-text lookup, merged in from the translations document.</summary>
    [JsonPropertyName("translations")]
    public Dictionary<string, string> Translations { get; set; } = [];

    /// <summary>When this snapshot was taken, so the app knows whether to refresh.</summary>
    [JsonPropertyName("fetchedAt")]
    public DateTimeOffset? FetchedAt { get; set; }
}

public sealed class MapPoiData
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("normalizedName")] public string NormalizedName { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("wiki")] public string? Wiki { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>Raid length in minutes. Feeds the trail's raid-length ceiling.</summary>
    [JsonPropertyName("raidDuration")] public int? RaidDuration { get; set; }

    [JsonPropertyName("players")] public string? Players { get; set; }

    [JsonPropertyName("extracts")] public List<ExtractData>? Extracts { get; set; }
    [JsonPropertyName("transits")] public List<TransitData>? Transits { get; set; }
    [JsonPropertyName("spawns")] public List<SpawnData>? Spawns { get; set; }
    [JsonPropertyName("bosses")] public List<BossData>? Bosses { get; set; }
    [JsonPropertyName("switches")] public List<SwitchData>? Switches { get; set; }
    [JsonPropertyName("hazards")] public List<HazardData>? Hazards { get; set; }
    [JsonPropertyName("locks")] public List<LockData>? Locks { get; set; }
    [JsonPropertyName("lootContainers")] public List<LootContainerData>? LootContainers { get; set; }
    [JsonPropertyName("stationaryWeapons")] public List<StationaryWeaponData>? StationaryWeapons { get; set; }
    [JsonPropertyName("btrStops")] public List<NamedPositionData>? BtrStops { get; set; }
}

public sealed class Vec3Data
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("z")] public double Z { get; set; }
}

public sealed class ExtractData
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>Localization key, e.g. <c>EXFIL_ZB013</c>.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>
    /// One of <c>pmc</c>, <c>scav</c>, <c>shared</c>. There is no separate co-op value; co-op
    /// extracts are <c>shared</c> and say so in their name, e.g. "Boiler Room Basement (Co-op)".
    /// </summary>
    [JsonPropertyName("faction")] public string? Faction { get; set; }

    [JsonPropertyName("position")] public Vec3Data? Position { get; set; }
    [JsonPropertyName("size")] public Vec3Data? Size { get; set; }
    [JsonPropertyName("outline")] public List<Vec3Data>? Outline { get; set; }
    [JsonPropertyName("top")] public double? Top { get; set; }
    [JsonPropertyName("bottom")] public double? Bottom { get; set; }

    /// <summary>Ids of switches that must be thrown before this extract works.</summary>
    [JsonPropertyName("switches")] public List<string>? Switches { get; set; }
}

public sealed class TransitData
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>Localization key, e.g. <c>CUS_TRANSIT_9_DESC</c>.</summary>
    [JsonPropertyName("description")] public string? Description { get; set; }

    [JsonPropertyName("conditions")] public string? Conditions { get; set; }

    /// <summary>Destination, as a BSG map id.</summary>
    [JsonPropertyName("map")] public string? Map { get; set; }

    [JsonPropertyName("position")] public Vec3Data? Position { get; set; }
    [JsonPropertyName("outline")] public List<Vec3Data>? Outline { get; set; }
    [JsonPropertyName("top")] public double? Top { get; set; }
    [JsonPropertyName("bottom")] public double? Bottom { get; set; }
}

/// <summary>
/// One spawn point. The heatmap is built from these.
/// </summary>
/// <remarks>
/// Observed values: <c>sides</c> is <c>pmc</c>, <c>scav</c> or <c>none</c>; <c>categories</c>
/// combines <c>player</c> (a human spawns here), <c>bot</c> (AI scav), <c>botpmc</c> (AI PMC)
/// and <c>boss</c>.
/// </remarks>
public sealed class SpawnData
{
    [JsonPropertyName("zoneName")] public string? ZoneName { get; set; }
    [JsonPropertyName("sides")] public List<string>? Sides { get; set; }
    [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
    [JsonPropertyName("position")] public Vec3Data? Position { get; set; }
}

public sealed class BossData
{
    /// <summary>Mob id, e.g. <c>bossBully</c>. Look up in <see cref="MapDataDocument.Mobs"/>.</summary>
    [JsonPropertyName("mob")] public string? Mob { get; set; }

    [JsonPropertyName("spawnChance")] public double? SpawnChance { get; set; }
    [JsonPropertyName("spawnTime")] public int? SpawnTime { get; set; }
    [JsonPropertyName("spawnTrigger")] public string? SpawnTrigger { get; set; }
    [JsonPropertyName("spawnLocations")] public List<BossSpawnLocationData>? SpawnLocations { get; set; }
    [JsonPropertyName("escorts")] public List<BossEscortData>? Escorts { get; set; }
}

public sealed class BossEscortData
{
    [JsonPropertyName("mob")] public string? Mob { get; set; }
}

/// <summary>
/// A zone a boss can spawn in. Unlike the GraphQL shape this carries the actual
/// <see cref="Positions"/>, so boss zones can be drawn without joining back to spawn zone names.
/// </summary>
public sealed class BossSpawnLocationData
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("spawnKey")] public string? SpawnKey { get; set; }
    [JsonPropertyName("chance")] public double? Chance { get; set; }
    [JsonPropertyName("positions")] public List<Vec3Data>? Positions { get; set; }
}

public sealed class SwitchData
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("switchType")] public string? SwitchType { get; set; }
    [JsonPropertyName("position")] public Vec3Data? Position { get; set; }

    /// <summary>What throwing this switch does, e.g. unlocking a specific extract.</summary>
    [JsonPropertyName("activates")] public List<SwitchOperationData>? Activates { get; set; }
}

public sealed class SwitchOperationData
{
    [JsonPropertyName("operation")] public string? Operation { get; set; }

    /// <summary>Extract id this operation applies to, when it targets one.</summary>
    [JsonPropertyName("extract")] public string? Extract { get; set; }
}

public sealed class HazardData
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("hazardType")] public string? HazardType { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("position")] public Vec3Data? Position { get; set; }
    [JsonPropertyName("outline")] public List<Vec3Data>? Outline { get; set; }
    [JsonPropertyName("top")] public double? Top { get; set; }
    [JsonPropertyName("bottom")] public double? Bottom { get; set; }
}

public sealed class LockData
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("lockType")] public string? LockType { get; set; }
    [JsonPropertyName("needsPower")] public bool? NeedsPower { get; set; }

    /// <summary>Item id of the key that opens it, or null when no key exists.</summary>
    [JsonPropertyName("key")] public string? Key { get; set; }

    [JsonPropertyName("position")] public Vec3Data? Position { get; set; }
}

public sealed class LootContainerData
{
    /// <summary>Container-type id; look up in <see cref="MapDataDocument.LootContainers"/>.</summary>
    [JsonPropertyName("lootContainer")] public string? LootContainer { get; set; }

    [JsonPropertyName("position")] public Vec3Data? Position { get; set; }
}

public sealed class StationaryWeaponData
{
    [JsonPropertyName("stationaryWeapon")] public string? StationaryWeapon { get; set; }
    [JsonPropertyName("position")] public Vec3Data? Position { get; set; }
}

public sealed class NamedIdData
{
    [JsonPropertyName("id")] public string? Id { get; set; }

    /// <summary>Usually a localization key rather than display text.</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("normalizedName")] public string? NormalizedName { get; set; }
}

public sealed class NamedPositionData
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("position")] public Vec3Data? Position { get; set; }
}
