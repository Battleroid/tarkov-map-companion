using System.Text.RegularExpressions;
using TarkovMapCompanion.Data.Models;
using TarkovMapCompanion.Diagnostics;
using TarkovMapCompanion.Localization;
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
    /// <summary>
    /// Fraction of a map's extracts that may reference one switch id before that id is treated as
    /// mis-assigned upstream and ignored.
    /// </summary>
    /// <remarks>
    /// A switch opens one door, so a real one is referenced by a single extract -- at most a couple
    /// when two extract volumes share a door. A switch the data hangs off half or more of a map's
    /// extracts cannot describe real gating; tarkov.dev currently does exactly this, attaching one
    /// Reserve switch id (a "reserve_electric_switcher_lever") to all 28 Customs extracts.
    /// </remarks>
    private const double MisassignedSwitchFraction = 0.5;

    /// <summary>
    /// Maps below this many extracts cannot trip the guard: "half of the extracts" is not meaningful
    /// on a one- or two-extract map, and suppressing there could erase genuine shared gating.
    /// </summary>
    private const int MisassignedSwitchMinimumExtracts = 3;

    /// <summary>
    /// Same-named extract records on one map whose positions sit within this many metres of each
    /// other are one physical exit and collapse into a single POI.
    /// </summary>
    /// <remarks>
    /// Upstream used to emit a single <c>faction:"shared"</c> record per exit; it now emits a
    /// <c>pmc</c> and a <c>scav</c> record instead, which would otherwise render as two rows. The
    /// worst measured separation within any same-name group in the bundled snapshot is 4.49 m (most
    /// are under a metre), so 10 m folds every real duplicate with a wide margin while still
    /// leaving genuinely distant same-named exits as separate POIs.
    /// </remarks>
    private const double ExtractMergeRadiusMeters = 10.0;

    /// <summary>
    /// Extract names that are plain codes, e.g. <c>ZB-1011</c> or <c>D-2</c>, which the game shows
    /// verbatim in every language.
    /// </summary>
    /// <remarks>
    /// The shipped Chinese table localises a couple of these anyway -- it renders <c>ZB-1011</c> as
    /// 工厂远角 -- but the client keeps the code, and a translated code is wrong at a glance in a
    /// list the player is scanning under time pressure. Codes therefore stay codes.
    /// </remarks>
    private static readonly Regex CodeExtractName = new(@"^[A-Z]+-\d+$", RegexOptions.Compiled);

    /// <summary>
    /// Mis-assigned switch ids already reported, keyed "<c>map|switchId</c>".
    /// </summary>
    /// <remarks>
    /// The POI list is rebuilt whenever a position, filter or raid change arrives, so without this
    /// the one warning that matters would repeat identically a few times a minute and bury itself.
    /// Once per process is enough for a defect that only changes when the data is refreshed.
    /// </remarks>
    private static readonly HashSet<string> WarnedMisassignedSwitches = new(StringComparer.Ordinal);
    private static readonly object WarnLock = new();

    public static IReadOnlyList<MapPoi> Build(
        GameMap map,
        MapPoiData data,
        MapDataStore store,
        ExtractNotesStore? notes = null,
        SeasonDocumentStore? seasonDocuments = null)
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
        AddSeasonDocuments(pois, map, seasonDocuments);

        return pois;
    }

    private static void AddExtracts(
        List<MapPoi> pois,
        GameMap map,
        MapPoiData data,
        MapDataStore store,
        ExtractNotesStore? notes)
    {
        var extracts = data.Extracts ?? [];

        // A switch id referenced by most of a map's extracts is an upstream mis-assignment, not real
        // gating (see MisassignedSwitchFraction). Find those ids up front so both the "activated by"
        // line and the conditional flag can ignore them.
        var misassignedSwitchIds = FindMisassignedSwitchIds(extracts, map);

        // Switch id -> readable name, so "activated by" can name the switch rather than hash it.
        var switchNames = (data.Switches ?? [])
            .Where(s => !string.IsNullOrEmpty(s.Id) && !misassignedSwitchIds.Contains(s.Id))
            .ToDictionary(s => s.Id, s => DescribeSwitch(s, store).Name, StringComparer.Ordinal);

        // Resolve each positioned extract once: its faction kind and both names. Two different raw
        // name keys can map to the same canonical English name (the pmc and scav halves of one
        // physical exit), so the merge below groups on the English name, never the raw key.
        var resolved = new List<ResolvedExtract>();
        foreach (var extract in extracts)
        {
            if (extract.Position is null)
                continue;

            // The English name is canonical: it is what the wiki notes are keyed by and what OCR
            // matches against. The Chinese name is only kept when it is a genuine translation.
            var (name, nameZh) = store.TranslatePair(extract.Name);
            resolved.Add(new ResolvedExtract(extract, KindFor(extract.Faction), name, nameZh));
        }

        // Group by English name, preserving first-seen order so the output is deterministic.
        var byName = new Dictionary<string, List<ResolvedExtract>>(StringComparer.Ordinal);
        var nameOrder = new List<string>();
        foreach (var member in resolved)
        {
            if (!byName.TryGetValue(member.EnglishName, out var list))
            {
                byName[member.EnglishName] = list = [];
                nameOrder.Add(member.EnglishName);
            }

            list.Add(member);
        }

        foreach (var name in nameOrder)
        {
            foreach (var cluster in ClusterCoincident(byName[name]))
                AddExtractPoi(pois, map, store, notes, cluster, switchNames, misassignedSwitchIds);
        }
    }

    /// <summary>
    /// One positioned extract with its faction and display names already resolved, ready to be
    /// merged with any coincident same-name siblings.
    /// </summary>
    /// <param name="ChineseName">
    /// The Chinese display name, or null when the data supplies none that differs from the English.
    /// A null here marks a member as untranslated, which keeps it from being chosen as the
    /// representative that names the merged POI.
    /// </param>
    private sealed record ResolvedExtract(
        ExtractData Source,
        PoiKind Kind,
        string EnglishName,
        string? ChineseName);

    /// <summary>
    /// Splits one same-name group into clusters of coincident records. Every member within
    /// <see cref="ExtractMergeRadiusMeters"/> of a cluster's first member joins it; anything farther
    /// away starts its own cluster, so a same-named but genuinely distant exit is never lost.
    /// </summary>
    private static IEnumerable<List<ResolvedExtract>> ClusterCoincident(List<ResolvedExtract> members)
    {
        var clusters = new List<ExtractCluster>();

        foreach (var member in members)
        {
            var position = member.Source.Position!;
            var home = clusters.FirstOrDefault(c => IsWithinMergeRadius(c.Reference, position));

            if (home is null)
                clusters.Add(new ExtractCluster(position, [member]));
            else
                home.Members.Add(member);
        }

        return clusters.Select(c => c.Members);
    }

    /// <summary>True when two game positions are close enough to be the same physical exit.</summary>
    private static bool IsWithinMergeRadius(Vec3Data a, Vec3Data b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;

        return Math.Sqrt(dx * dx + dy * dy + dz * dz) <= ExtractMergeRadiusMeters;
    }

    /// <summary>A set of coincident same-name members and the position that identified them.</summary>
    private sealed class ExtractCluster(Vec3Data reference, List<ResolvedExtract> members)
    {
        public Vec3Data Reference { get; } = reference;
        public List<ResolvedExtract> Members { get; } = members;
    }

    /// <summary>
    /// Builds one POI from a cluster of coincident same-name extract records.
    /// </summary>
    private static void AddExtractPoi(
        List<MapPoi> pois,
        GameMap map,
        MapDataStore store,
        ExtractNotesStore? notes,
        List<ResolvedExtract> cluster,
        IReadOnlyDictionary<string, string> switchNames,
        HashSet<string> misassignedSwitchIds)
    {
        // The representative supplies the position and the localized name. Choosing a translated
        // member is what stops an untranslated sibling (whose Chinese merely echoes the English)
        // from blanking out the localized name.
        var representative = ChooseRepresentative(cluster);
        var name = representative.EnglishName;

        // If every member shares one faction kind, keep it; the pmc+scav pair has no single kind,
        // so it becomes shared, which is how the formerly-combined exits are modeled.
        var kinds = cluster.Select(m => m.Kind).Distinct().ToArray();
        var kind = kinds.Length == 1 ? kinds[0] : PoiKind.ExtractShared;

        var details = new List<string>();
        var detailsZh = new List<string>();

        // English line, with its Chinese counterpart when the overlay supplies one. A missing
        // Chinese value falls back to the English text so no line ever disappears.
        void AddLine(string english, string? chinese = null)
        {
            details.Add(english);
            detailsZh.Add(string.IsNullOrWhiteSpace(chinese) ? english : chinese!);
        }

        // Union of every member's switches, in order. A map-wide switch id is upstream noise:
        // dropping it here -- rather than letting it fall through to "requires a switch" -- is what
        // keeps "activated by" off extracts whose only switch is the bogus one.
        var gatingSwitches = 0;
        var seenSwitchIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in cluster)
        {
            foreach (var switchId in member.Source.Switches ?? [])
            {
                if (string.IsNullOrEmpty(switchId) || misassignedSwitchIds.Contains(switchId))
                    continue;

                if (!seenSwitchIds.Add(switchId))
                    continue;

                gatingSwitches++;

                // The wrapper is localized at build time; the Chinese list mirrors it exactly.
                AddLine(switchNames.TryGetValue(switchId, out var switchName)
                    ? Localizer.Loc("Poi.ActivatedBy", switchName)
                    : Localizer.Loc("Poi.RequiresSwitch"));
            }
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
                AddLine(
                    Localizer.LocEnglish("Poi.Needs", note.Requirement),
                    string.IsNullOrWhiteSpace(note.RequirementZh)
                        ? null
                        : Localizer.Loc("Poi.Needs", note.RequirementZh));
                conditional = true;
            }

            if (!string.IsNullOrWhiteSpace(note.Availability))
            {
                AddLine(
                    Localizer.LocEnglish("Poi.AlwaysOpen", note.Availability),
                    string.IsNullOrWhiteSpace(note.AvailabilityZh)
                        ? null
                        : Localizer.Loc("Poi.AlwaysOpen", note.AvailabilityZh));
                conditional = true;
            }
            else if (note.AlwaysAvailable == false)
            {
                AddLine(Localizer.Loc("Poi.NotAlwaysOpen"));
                conditional = true;
            }

            if (note.SingleUse == true)
            {
                AddLine(Localizer.Loc("Poi.SingleUse"));
                singleUse = true;
            }
        }
        else if (name.Contains("Co-op", StringComparison.OrdinalIgnoreCase))
        {
            // Fallback for an extract the notes file has not caught up with.
            AddLine(Localizer.Loc("Poi.Coop"));
            conditional = true;
        }

        var position = representative.Source.Position!;

        pois.Add(new MapPoi
        {
            Kind = kind,
            Id = representative.Source.Id,
            Name = name,

            // A code name carries no translation in any language: the display falls back to the code
            // itself, so ZB-1011 cannot arrive as 工厂远角 just because a sibling record's Chinese
            // happened to translate it.
            NameZh = CodeExtractName.IsMatch(name) ? null : representative.ChineseName,
            Position = ToGame(position),
            Base = map.Projection.ToBase(position.X, position.Z),
            Outline = ToOutline(map, representative.Source.Outline),
            Elevation = ToElevation(representative.Source.Bottom, representative.Source.Top),
            Details = details,
            DetailsZh = detailsZh,

            // A switch-gated exit is conditional too, even without a wiki note. Only switches we
            // actually kept count; a suppressed mis-assigned id must not force every row
            // conditional.
            IsConditional = conditional || gatingSwitches > 0,
            IsSingleUse = singleUse,
        });
    }

    /// <summary>
    /// Picks the member whose names and position the merged POI uses.
    /// </summary>
    /// <remarks>
    /// A member with a real Chinese translation wins, so an untranslated sibling whose Chinese
    /// merely echoes the English can never blank out the localized name. If none is translated, the
    /// pmc half is preferred, and failing that the first member.
    /// </remarks>
    private static ResolvedExtract ChooseRepresentative(List<ResolvedExtract> cluster)
    {
        foreach (var member in cluster)
        {
            if (member.ChineseName is not null)
                return member;
        }

        foreach (var member in cluster)
        {
            if (member.Kind == PoiKind.ExtractPmc)
                return member;
        }

        return cluster[0];
    }

    /// <summary>Maps a raw extract faction onto the layer it belongs in.</summary>
    private static PoiKind KindFor(string? faction) => faction?.ToLowerInvariant() switch
    {
        "pmc" => PoiKind.ExtractPmc,
        "scav" => PoiKind.ExtractScav,
        _ => PoiKind.ExtractShared,
    };

    /// <summary>
    /// Switch ids upstream has mis-assigned: attached to so many of a map's extracts that they cannot
    /// describe real gating. See <see cref="MisassignedSwitchFraction"/>.
    /// </summary>
    private static HashSet<string> FindMisassignedSwitchIds(List<ExtractData> extracts, GameMap map)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        // A two-extract map must never be able to trip this: "half" is meaningless that small.
        if (extracts.Count < MisassignedSwitchMinimumExtracts)
            return result;

        var references = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var extract in extracts)
        {
            foreach (var switchId in extract.Switches ?? [])
            {
                if (string.IsNullOrEmpty(switchId))
                    continue;

                references.TryGetValue(switchId, out var count);
                references[switchId] = count + 1;
            }
        }

        foreach (var (switchId, count) in references)
        {
            if (count < extracts.Count * MisassignedSwitchFraction)
                continue;

            result.Add(switchId);

            // One warning per bogus id per map per process: app.log should show the defect is being
            // handled, not narrate it again on every rebuild.
            lock (WarnLock)
            {
                if (WarnedMisassignedSwitches.Add($"{map.NormalizedName}|{switchId}"))
                {
                    Log.Warn(
                        $"map '{map.NormalizedName}': switch id '{switchId}' is referenced by {count} of " +
                        $"{extracts.Count} extracts and looks mis-assigned upstream; ignoring it");
                }
            }
        }

        return result;
    }

    private static void AddTransits(List<MapPoi> pois, GameMap map, MapPoiData data, MapDataStore store)
    {
        foreach (var transit in data.Transits ?? [])
        {
            if (transit.Position is null)
                continue;

            var destination = store.NormalizedNameForId(transit.Map);
            var (name, nameZh) = store.TranslatePair(transit.Description);
            var details = new List<string>();

            if (destination is not null)
                details.Add(Localizer.Loc("Poi.LeadsTo", MapNames.Display(destination)));

            if (!string.IsNullOrWhiteSpace(transit.Conditions))
                details.Add(transit.Conditions);

            pois.Add(new MapPoi
            {
                Kind = PoiKind.Transit,
                Id = transit.Id,
                Name = name,
                NameZh = nameZh,
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
            var chance = boss.SpawnChance is { } c ? Localizer.Loc("Poi.SpawnChanceMap", c * 100) : null;

            foreach (var location in boss.SpawnLocations ?? [])
            {
                var zoneChance = location.Chance is { } lc && lc > 0 ? Localizer.Loc("Poi.SpawnChanceZone", lc * 100) : null;

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

            var (name, nameZh) = DescribeSwitch(sw, store);

            pois.Add(new MapPoi
            {
                Kind = PoiKind.Switch,
                Id = sw.Id,
                Name = name,
                NameZh = nameZh,
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
                details.Add(Localizer.Loc("Poi.NeedsPower"));

            pois.Add(new MapPoi
            {
                Kind = PoiKind.Lock,
                Id = l.Id,
                Name = string.IsNullOrWhiteSpace(l.LockType) ? Localizer.Loc("Poi.Locked") : Capitalize(l.LockType),
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

            var (name, nameZh) = store.LootContainerPair(container.LootContainer);

            pois.Add(new MapPoi
            {
                Kind = PoiKind.LootContainer,
                Name = name,
                NameZh = nameZh,
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

            var (name, nameZh) = store.StationaryWeaponPair(weapon.StationaryWeapon);

            pois.Add(new MapPoi
            {
                Kind = PoiKind.StationaryWeapon,
                Name = name,
                NameZh = nameZh,
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

            var (name, nameZh) = string.IsNullOrWhiteSpace(stop.Name)
                ? (Localizer.Loc("Poi.BtrStop"), (string?)null)
                : store.TranslatePair(stop.Name);

            pois.Add(new MapPoi
            {
                Kind = PoiKind.BtrStop,
                Name = name,
                NameZh = nameZh,
                Position = ToGame(stop.Position),
                Base = map.Projection.ToBase(stop.Position.X, stop.Position.Z),
            });
        }
    }

    private static void AddSeasonDocuments(List<MapPoi> pois, GameMap map, SeasonDocumentStore? store)
    {
        if (store is null)
            return;

        foreach (var point in store.FindFor(map.NormalizedName))
        {
            // The source is Chinese-only metadata; both detail lists carry the same lines so the
            // display shows them whether English or Chinese is active.
            var details = new List<string>();

            if (!string.IsNullOrWhiteSpace(point.TypeZh))
                details.Add(point.TypeZh!);

            if (!string.IsNullOrWhiteSpace(point.Floor))
                details.Add($"楼层 {point.Floor}");

            if (!string.IsNullOrWhiteSpace(point.KeyRequiredZh))
                details.Add($"需钥匙：{point.KeyRequiredZh}");

            var name = point.NameZh ?? "";

            pois.Add(new MapPoi
            {
                Kind = PoiKind.SeasonDocuments,
                Id = point.Id,
                Name = name,
                NameZh = name,
                Position = new GamePosition(point.X, 0, point.Z),
                Base = map.Projection.ToBase(point.X, point.Z),
                Details = details,
                DetailsZh = details,
                IsUnverified = !point.Confirmed,
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
            return Localizer.Loc("Poi.SpawnBoss");

        var side = sides.Contains("pmc") ? Localizer.Loc("Poi.SidePmc")
            : sides.Contains("scav") ? Localizer.Loc("Poi.SideScav")
            : Localizer.Loc("Poi.SideUnknown");

        // "player" means a human can spawn here; anything else is AI only.
        var who = categories.Contains("player") ? "" : Localizer.Loc("Poi.SpawnAi");

        return Localizer.Loc("Poi.SpawnLabel", side, who);
    }

    /// <summary>
    /// Switch names in the data are raw scene object paths such as
    /// <c>switch_custom_DesignStuff_00034_reserve_electric_switcher_lever</c>, which are useless
    /// in a tooltip. Fall back to the switch type when the name is one of those.
    /// </summary>
    private static (string Name, string? NameZh) DescribeSwitch(SwitchData sw, MapDataStore store)
    {
        var (translated, translatedZh) = store.TranslatePair(sw.Name);

        var looksInternal = string.IsNullOrWhiteSpace(translated)
            || translated.StartsWith("switch_", StringComparison.OrdinalIgnoreCase)
            || translated.Contains("DesignStuff", StringComparison.OrdinalIgnoreCase);

        if (!looksInternal)
            return (translated, translatedZh);

        return (string.IsNullOrWhiteSpace(sw.SwitchType)
            ? Localizer.Loc("Poi.Switch")
            : Localizer.Loc("Poi.SwitchLabel", Capitalize(sw.SwitchType)), null);
    }

    /// <summary>Hazard names are role paths like <c>ScavRole/Marksman</c>.</summary>
    private static string PrettifyHazard(HazardData hazard)
    {
        var type = hazard.HazardType?.ToLowerInvariant();

        return type switch
        {
            "sniper" => Localizer.Loc("Poi.HazardSniper"),
            "minefield" => Localizer.Loc("Poi.HazardMinefield"),
            "mortar" => Localizer.Loc("Poi.HazardMortar"),
            _ when !string.IsNullOrWhiteSpace(type) => Capitalize(type),
            _ => hazard.Name?.Split('/').LastOrDefault() ?? Localizer.Loc("Poi.Hazard"),
        };
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
