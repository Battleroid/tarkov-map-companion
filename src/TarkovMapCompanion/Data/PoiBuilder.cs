using TarkovMapCompanion.Data.Models;
using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Data;

/// <summary>
/// Turns the raw <c>json.tarkov.dev</c> payload for one map into projected, display-ready POIs.
/// </summary>
/// <remarks>
/// This is where localization keys become names, id references become text, and game coordinates
/// become base-space points. Doing it once per map change keeps the render loop free of lookups.
/// </remarks>
public static class PoiBuilder
{
    public static IReadOnlyList<MapPoi> Build(
        GameMap map,
        MapPoiData data,
        MapDataStore store,
        ExtractNotesStore? notes = null)
    {
        var pois = new List<MapPoi>();

        AddExtracts(pois, map, data, store, notes);
        AddTransits(pois, map, data, store);
        AddSpawns(pois, map, data);
        AddBossZones(pois, map, data, store);
        AddSwitches(pois, map, data, store);
        AddHazards(pois, map, data);
        AddLocks(pois, map, data);
        AddLootContainers(pois, map, data, store);
        AddStationaryWeapons(pois, map, data, store);
        AddBtrStops(pois, map, data, store);

        return pois;
    }

    private static void AddExtracts(
        List<MapPoi> pois,
        GameMap map,
        MapPoiData data,
        MapDataStore store,
        ExtractNotesStore? notes)
    {
        // Switch id -> readable name, so "activated by" can name the switch rather than hash it.
        var switchNames = (data.Switches ?? [])
            .Where(s => !string.IsNullOrEmpty(s.Id))
            .ToDictionary(s => s.Id, s => DescribeSwitch(s, store), StringComparer.Ordinal);

        foreach (var extract in data.Extracts ?? [])
        {
            if (extract.Position is null)
                continue;

            var kind = extract.Faction?.ToLowerInvariant() switch
            {
                "pmc" => PoiKind.ExtractPmc,
                "scav" => PoiKind.ExtractScav,
                _ => PoiKind.ExtractShared,
            };

            var name = store.Translate(extract.Name);
            var details = new List<string>();

            foreach (var switchId in extract.Switches ?? [])
            {
                details.Add(switchNames.TryGetValue(switchId, out var switchName)
                    ? $"Activated by: {switchName}"
                    : "Requires a switch to be thrown");
            }

            // Everything the position data cannot tell us: payment, required items, timed
            // windows, co-op partners. Comes from the wiki via ExtractNotesStore.
            var note = notes?.Find(map.NormalizedName, name);
            var conditional = false;
            var singleUse = false;

            if (note is not null)
            {
                if (!string.IsNullOrWhiteSpace(note.Requirement))
                {
                    details.Add($"Needs: {note.Requirement}");
                    conditional = true;
                }

                if (!string.IsNullOrWhiteSpace(note.Availability))
                {
                    details.Add($"Always open? {note.Availability}");
                    conditional = true;
                }
                else if (note.AlwaysAvailable == false)
                {
                    details.Add("Not always open");
                    conditional = true;
                }

                if (note.SingleUse == true)
                {
                    details.Add("Single use: only one player can take it");
                    singleUse = true;
                }
            }
            else if (name.Contains("Co-op", StringComparison.OrdinalIgnoreCase))
            {
                // Fallback for an extract the notes file has not caught up with.
                details.Add("Co-op: needs a player of the other faction to open it with you");
                conditional = true;
            }

            pois.Add(new MapPoi
            {
                Kind = kind,
                Id = extract.Id,
                Name = name,
                Position = ToGame(extract.Position),
                Base = map.Projection.ToBase(extract.Position.X, extract.Position.Z),
                Outline = ToOutline(map, extract.Outline),
                Elevation = ToElevation(extract.Bottom, extract.Top),
                Details = details,

                // A switch-gated exit is conditional too, even without a wiki note.
                IsConditional = conditional || extract.Switches is { Count: > 0 },
                IsSingleUse = singleUse,
            });
        }
    }

    private static void AddTransits(List<MapPoi> pois, GameMap map, MapPoiData data, MapDataStore store)
    {
        foreach (var transit in data.Transits ?? [])
        {
            if (transit.Position is null)
                continue;

            var destination = store.NormalizedNameForId(transit.Map);
            var details = new List<string>();

            if (destination is not null)
                details.Add($"Leads to {GameMap.ToDisplayName(destination)}");

            if (!string.IsNullOrWhiteSpace(transit.Conditions))
                details.Add(transit.Conditions);

            pois.Add(new MapPoi
            {
                Kind = PoiKind.Transit,
                Id = transit.Id,
                Name = store.Translate(transit.Description),
                Position = ToGame(transit.Position),
                Base = map.Projection.ToBase(transit.Position.X, transit.Position.Z),
                Outline = ToOutline(map, transit.Outline),
                Elevation = ToElevation(transit.Bottom, transit.Top),
                Details = details,
                DestinationMap = destination,
            });
        }
    }

    private static void AddSpawns(List<MapPoi> pois, GameMap map, MapPoiData data)
    {
        foreach (var spawn in data.Spawns ?? [])
        {
            if (spawn.Position is null)
                continue;

            pois.Add(new MapPoi
            {
                Kind = PoiKind.Spawn,
                Name = DescribeSpawn(spawn),
                Position = ToGame(spawn.Position),
                Base = map.Projection.ToBase(spawn.Position.X, spawn.Position.Z),
            });
        }
    }

    private static void AddBossZones(List<MapPoi> pois, GameMap map, MapPoiData data, MapDataStore store)
    {
        foreach (var boss in data.Bosses ?? [])
        {
            var name = store.MobName(boss.Mob);
            var chance = boss.SpawnChance is { } c ? $"{c * 100:F0}% to spawn on this map" : null;

            foreach (var location in boss.SpawnLocations ?? [])
            {
                var zoneChance = location.Chance is { } lc && lc > 0 ? $"{lc * 100:F0}% in this zone" : null;

                var details = new[] { chance, zoneChance }.OfType<string>().ToArray();

                foreach (var position in location.Positions ?? [])
                {
                    pois.Add(new MapPoi
                    {
                        Kind = PoiKind.BossZone,
                        Name = name,
                        Position = ToGame(position),
                        Base = map.Projection.ToBase(position.X, position.Z),
                        Details = details,
                    });
                }
            }
        }
    }

    private static void AddSwitches(List<MapPoi> pois, GameMap map, MapPoiData data, MapDataStore store)
    {
        foreach (var sw in data.Switches ?? [])
        {
            if (sw.Position is null)
                continue;

            pois.Add(new MapPoi
            {
                Kind = PoiKind.Switch,
                Id = sw.Id,
                Name = DescribeSwitch(sw, store),
                Position = ToGame(sw.Position),
                Base = map.Projection.ToBase(sw.Position.X, sw.Position.Z),
            });
        }
    }

    private static void AddHazards(List<MapPoi> pois, GameMap map, MapPoiData data)
    {
        foreach (var hazard in data.Hazards ?? [])
        {
            if (hazard.Position is null)
                continue;

            pois.Add(new MapPoi
            {
                Kind = PoiKind.Hazard,
                Name = PrettifyHazard(hazard),
                Position = ToGame(hazard.Position),
                Base = map.Projection.ToBase(hazard.Position.X, hazard.Position.Z),
                Outline = ToOutline(map, hazard.Outline),
                Elevation = ToElevation(hazard.Bottom, hazard.Top),
            });
        }
    }

    private static void AddLocks(List<MapPoi> pois, GameMap map, MapPoiData data)
    {
        foreach (var l in data.Locks ?? [])
        {
            if (l.Position is null)
                continue;

            var details = new List<string>();
            if (l.NeedsPower == true)
                details.Add("Needs power");

            pois.Add(new MapPoi
            {
                Kind = PoiKind.Lock,
                Id = l.Id,
                Name = string.IsNullOrWhiteSpace(l.LockType) ? "Locked" : Capitalize(l.LockType),
                Position = ToGame(l.Position),
                Base = map.Projection.ToBase(l.Position.X, l.Position.Z),
                Details = details,
            });
        }
    }

    private static void AddLootContainers(List<MapPoi> pois, GameMap map, MapPoiData data, MapDataStore store)
    {
        foreach (var container in data.LootContainers ?? [])
        {
            if (container.Position is null)
                continue;

            pois.Add(new MapPoi
            {
                Kind = PoiKind.LootContainer,
                Name = store.LootContainerName(container.LootContainer),
                Position = ToGame(container.Position),
                Base = map.Projection.ToBase(container.Position.X, container.Position.Z),
            });
        }
    }

    private static void AddStationaryWeapons(List<MapPoi> pois, GameMap map, MapPoiData data, MapDataStore store)
    {
        foreach (var weapon in data.StationaryWeapons ?? [])
        {
            if (weapon.Position is null)
                continue;

            pois.Add(new MapPoi
            {
                Kind = PoiKind.StationaryWeapon,
                Name = store.StationaryWeaponName(weapon.StationaryWeapon),
                Position = ToGame(weapon.Position),
                Base = map.Projection.ToBase(weapon.Position.X, weapon.Position.Z),
            });
        }
    }

    private static void AddBtrStops(List<MapPoi> pois, GameMap map, MapPoiData data, MapDataStore store)
    {
        foreach (var stop in data.BtrStops ?? [])
        {
            if (stop.Position is null)
                continue;

            pois.Add(new MapPoi
            {
                Kind = PoiKind.BtrStop,
                Name = string.IsNullOrWhiteSpace(stop.Name) ? "BTR stop" : store.Translate(stop.Name),
                Position = ToGame(stop.Position),
                Base = map.Projection.ToBase(stop.Position.X, stop.Position.Z),
            });
        }
    }

    // ---- Helpers ------------------------------------------------------------

    private static GamePosition ToGame(Vec3Data v) => new(v.X, v.Y, v.Z);

    private static IReadOnlyList<MapPoint>? ToOutline(GameMap map, List<Vec3Data>? outline) =>
        outline is { Count: > 2 }
            ? outline.Select(p => map.Projection.ToBase(p.X, p.Z)).ToArray()
            : null;

    private static (double Bottom, double Top)? ToElevation(double? bottom, double? top) =>
        bottom is { } b && top is { } t ? (Math.Min(b, t), Math.Max(b, t)) : null;

    /// <summary>
    /// Turns the raw category and side lists into something readable, e.g. "PMC spawn" or
    /// "Scav spawn (AI)".
    /// </summary>
    private static string DescribeSpawn(SpawnData spawn)
    {
        var categories = spawn.Categories ?? [];
        var sides = spawn.Sides ?? [];

        if (categories.Contains("boss"))
            return "Boss spawn";

        var side = sides.Contains("pmc") ? "PMC"
            : sides.Contains("scav") ? "Scav"
            : "Unknown";

        // "player" means a human can spawn here; anything else is AI only.
        var who = categories.Contains("player") ? "" : " (AI)";

        return $"{side} spawn{who}";
    }

    /// <summary>
    /// Switch names in the data are raw scene object paths such as
    /// <c>switch_custom_DesignStuff_00034_reserve_electric_switcher_lever</c>, which are useless
    /// in a tooltip. Fall back to the switch type when the name is one of those.
    /// </summary>
    private static string DescribeSwitch(SwitchData sw, MapDataStore store)
    {
        var translated = store.Translate(sw.Name);

        var looksInternal = string.IsNullOrWhiteSpace(translated)
            || translated.StartsWith("switch_", StringComparison.OrdinalIgnoreCase)
            || translated.Contains("DesignStuff", StringComparison.OrdinalIgnoreCase);

        if (!looksInternal)
            return translated;

        return string.IsNullOrWhiteSpace(sw.SwitchType) ? "Switch" : $"{Capitalize(sw.SwitchType)} switch";
    }

    /// <summary>Hazard names are role paths like <c>ScavRole/Marksman</c>.</summary>
    private static string PrettifyHazard(HazardData hazard)
    {
        var type = hazard.HazardType?.ToLowerInvariant();

        return type switch
        {
            "sniper" => "Sniper scav",
            "minefield" => "Minefield",
            "mortar" => "Mortar strike zone",
            _ when !string.IsNullOrWhiteSpace(type) => Capitalize(type),
            _ => hazard.Name?.Split('/').LastOrDefault() ?? "Hazard",
        };
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
