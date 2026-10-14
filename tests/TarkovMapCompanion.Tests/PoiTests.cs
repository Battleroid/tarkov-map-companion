using TarkovMapCompanion.Data;
using TarkovMapCompanion.Data.Models;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Exercises the POI pipeline against the snapshot that actually ships, so a bad regeneration or
/// an upstream shape change fails here rather than showing up as an empty map.
/// </summary>
public sealed class PoiTests : IDisposable
{
    private static readonly MapCatalog Catalog = MapCatalog.LoadEmbedded();

    // The app defaults to Simplified Chinese now; the detail strings asserted below are English, so
    // this class pins the language it actually exercises.
    private readonly LanguageScope _language = new(AppLanguage.English);

    public void Dispose() => _language.Dispose();

    private static MapDataStore LoadStore()
    {
        // Point the cache somewhere empty so this reads the embedded snapshot, not whatever the
        // developer's machine happens to have downloaded.
        var store = new MapDataStore(
            new AppSettings { AllowNetwork = false },
            cacheDirectory: Path.Combine(Path.GetTempPath(), "tmc-tests-nocache", Guid.NewGuid().ToString("N")));

        store.LoadLocal();
        return store;
    }

    /// <summary>
    /// A synthetic single-map payload: one entry per extract plus the switch definitions those
    /// extracts reference, so <see cref="PoiBuilder"/> has real ids and readable names to work with.
    /// </summary>
    private static MapPoiData SyntheticMap(params (string Name, string[] Switches)[] extracts)
    {
        var switchIds = extracts.SelectMany(e => e.Switches).Distinct(StringComparer.Ordinal).ToArray();

        return new MapPoiData
        {
            NormalizedName = "customs",
            Extracts = extracts.Select((e, i) => new ExtractData
            {
                Id = $"ex_{i}",
                Name = e.Name,
                Faction = "shared",
                Position = new Vec3Data { X = i, Y = 0, Z = 0 },
                Switches = e.Switches.Length == 0 ? null : e.Switches.ToList(),
            }).ToList(),
            Switches = switchIds
                .Select(id => new SwitchData { Id = id, Name = "Power Switch", SwitchType = "Power" })
                .ToList(),
        };
    }

    /// <summary>One raw extract for <see cref="SyntheticExtractMap"/>: name, faction and position.</summary>
    private sealed record SyntheticExtract(string Name, string Faction, double X, double Z, string[] Switches);

    /// <summary>
    /// A synthetic single-map payload with full control over faction and position, for pinning the
    /// coincident-extract merge rule without depending on the shipped snapshot's exact names.
    /// </summary>
    private static MapPoiData SyntheticExtractMap(params SyntheticExtract[] extracts)
    {
        var switchIds = extracts.SelectMany(e => e.Switches).Distinct(StringComparer.Ordinal).ToArray();

        return new MapPoiData
        {
            NormalizedName = "customs",
            Extracts = extracts.Select((e, i) => new ExtractData
            {
                Id = $"ex_{i}",
                Name = e.Name,
                Faction = e.Faction,
                Position = new Vec3Data { X = e.X, Y = 0, Z = e.Z },
                Switches = e.Switches.Length == 0 ? null : e.Switches.ToList(),
            }).ToList(),
            Switches = switchIds
                .Select(id => new SwitchData { Id = id, Name = "Power Switch", SwitchType = "Power" })
                .ToList(),
        };
    }

    [Fact]
    public void TheBundledSnapshotLoadsAndCoversTheMapsWeRender()
    {
        var store = LoadStore();

        Assert.Equal("bundled snapshot", store.Origin);

        foreach (var map in Catalog.Maps)
        {
            // Terminal and Icebreaker are new and carry spawns but no extracts yet; every other
            // map must at least be present in the data.
            Assert.True(
                store.ForMap(map.NormalizedName) is not null,
                $"no POI data for {map.NormalizedName}");
        }
    }

    [Fact]
    public void RaidDurationsMatchTheGame()
    {
        var store = LoadStore();

        Assert.Equal(20, store.RaidDurationMinutes("factory"));
        Assert.Equal(35, store.RaidDurationMinutes("customs"));
        Assert.Equal(45, store.RaidDurationMinutes("shoreline"));
        Assert.Equal(40, store.RaidDurationMinutes("streets-of-tarkov"));
    }

    [Fact]
    public void CustomsExtracts_AreNamedTranslatedAndFactioned()
    {
        var store = LoadStore();
        var map = Catalog.Find("customs")!;
        var pois = PoiBuilder.Build(map, store.ForMap("customs")!, store);

        var extracts = pois.Where(p => p.IsExtract).ToArray();

        // Upstream now emits a pmc and a scav record for the exits it used to emit as one shared
        // record; the builder folds each coincident same-name pair back into one row. Customs ships
        // 28 raw records across 6 duplicated exits, so 22 rows is the post-merge count.
        Assert.Equal(22, extracts.Length);

        // Localization keys must have been resolved; a raw EXFIL_ key means the merge broke.
        Assert.DoesNotContain(extracts, e => e.Name.StartsWith("EXFIL", StringComparison.Ordinal));

        // Crossroads used to arrive as one shared record; upstream then split it into pmc and scav
        // records. Both must collapse into a single shared exit carrying the translated name, not
        // the untranslated scav sibling's English text.
        var crossroads = extracts.Single(e => e.Name == "Crossroads");
        Assert.Equal(PoiKind.ExtractShared, crossroads.Kind);
        Assert.Equal("十字路口", crossroads.NameZh);

        // The reverse case: here the pmc sibling is the untranslated one and the scav sibling
        // carries the translation, so the merge must still pick the translated name.
        var railroad = extracts.Single(e => e.Name == "Railroad to Reserve");
        Assert.Equal("通往军事基地的铁路", railroad.NameZh);

        // ZB-1011 is a code the game shows verbatim, but the Chinese table translates its scav half
        // to 工厂远角. A code stays a code: the merged row must not carry that translation, so the
        // display falls back to ZB-1011 itself in every language.
        var zb1011 = extracts.Single(e => e.Name == "ZB-1011");
        Assert.Null(zb1011.NameZh);
        Assert.Equal("ZB-1011", zb1011.DisplayName);

        // Names as the refreshed snapshot spells them (upstream renamed Scav Checkpoint to
        // Reserve Checkpoint).
        Assert.Contains(extracts, e => e.Name == "Dorms V-Ex" && e.Kind == PoiKind.ExtractPmc);
        Assert.Contains(extracts, e => e.Name == "Reserve Checkpoint" && e.Kind == PoiKind.ExtractScav);
        Assert.Contains(extracts, e => e.Name.StartsWith("Boiler Room Basement", StringComparison.Ordinal)
                                       && e.Kind == PoiKind.ExtractShared);
    }

    /// <summary>
    /// Upstream's new shape: one physical exit arrives as a coincident pmc and scav record with the
    /// same English name. They must collapse into one shared row, not two.
    /// </summary>
    [Fact]
    public void CoincidentPmcAndScavExtracts_MergeIntoOneSharedPoi()
    {
        var store = LoadStore();
        var map = Catalog.Find("customs")!;

        var data = SyntheticExtractMap(
            new SyntheticExtract("Shared Exit", "pmc", 0, 0, []),
            new SyntheticExtract("Shared Exit", "scav", 0.3, 0.2, []));

        var pois = PoiBuilder.Build(map, data, store, notes: null);
        var extracts = pois.Where(p => p.IsExtract).ToArray();

        var merged = Assert.Single(extracts);
        Assert.Equal(PoiKind.ExtractShared, merged.Kind);
        Assert.Equal("Shared Exit", merged.Name);
    }

    /// <summary>
    /// Coincident duplicates of the same faction (Customs has a scav+scav pair) also collapse, and
    /// keep that faction rather than being promoted to shared.
    /// </summary>
    [Fact]
    public void CoincidentSameFactionExtracts_MergeIntoOnePoiOfThatFaction()
    {
        var store = LoadStore();
        var map = Catalog.Find("customs")!;

        var data = SyntheticExtractMap(
            new SyntheticExtract("Scav Exit", "scav", 5, 5, []),
            new SyntheticExtract("Scav Exit", "scav", 5.4, 5.3, []));

        var pois = PoiBuilder.Build(map, data, store, notes: null);
        var merged = pois.Single(p => p.IsExtract);

        Assert.Equal(PoiKind.ExtractScav, merged.Kind);
    }

    /// <summary>
    /// Same name but genuinely far apart is not a duplicate: both must survive as separate POIs.
    /// </summary>
    [Fact]
    public void SameNameExtractsFarApart_StaySeparatePois()
    {
        var store = LoadStore();
        var map = Catalog.Find("customs")!;

        var data = SyntheticExtractMap(
            new SyntheticExtract("Far Exit", "pmc", 0, 0, []),
            new SyntheticExtract("Far Exit", "pmc", 50, 0, []));

        var pois = PoiBuilder.Build(map, data, store, notes: null);
        var extracts = pois.Where(p => p.IsExtract).ToArray();

        Assert.Equal(2, extracts.Length);
        Assert.All(extracts, e => Assert.Equal(PoiKind.ExtractPmc, e.Kind));
    }

    /// <summary>
    /// When coincident records carry different switches, the merged POI takes the union of them.
    /// </summary>
    [Fact]
    public void MergingExtracts_UnionsTheirSwitches()
    {
        var store = LoadStore();
        var map = Catalog.Find("customs")!;

        // Two unrelated extracts keep a switch on one of the merged records below half this map's
        // extracts, so the mis-assignment guard cannot strip them.
        var data = SyntheticExtractMap(
            new SyntheticExtract("Union Exit", "pmc", 0, 0, ["s1"]),
            new SyntheticExtract("Union Exit", "scav", 0.2, 0.1, ["s2"]),
            new SyntheticExtract("Other A", "pmc", 100, 0, []),
            new SyntheticExtract("Other B", "scav", 200, 0, []));

        var pois = PoiBuilder.Build(map, data, store, notes: null);

        var merged = pois.Single(p => p.Name == "Union Exit");
        Assert.Equal(PoiKind.ExtractShared, merged.Kind);
        Assert.True(merged.IsConditional);
        Assert.Equal(2, merged.Details.Count(d => d.StartsWith("Activated by", StringComparison.Ordinal)));
    }

    [Fact]
    public void CoopConditionIsInferredFromTheNameWhenThereIsNoWikiNote()
    {
        // Fallback path only: real conditions come from ExtractNotesStore, covered separately.
        var store = LoadStore();
        var map = Catalog.Find("customs")!;
        var pois = PoiBuilder.Build(map, store.ForMap("customs")!, store, notes: null);

        var coop = pois.Single(p => p.Name.Contains("Boiler Room Basement", StringComparison.Ordinal));

        Assert.Equal(PoiKind.ExtractShared, coop.Kind);
        Assert.True(coop.IsConditional);
        Assert.Contains(coop.Details, d => d.Contains("Co-op", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WikiNotesTakePrecedenceOverTheNameHeuristic()
    {
        var store = LoadStore();
        var notes = new ExtractNotesStore();
        notes.Load();

        var map = Catalog.Find("customs")!;
        var pois = PoiBuilder.Build(map, store.ForMap("customs")!, store, notes);

        // With notes loaded the flare extract gets the real requirement, which the old
        // name-based guess could never have produced.
        var flare = pois.Single(p => p.Name.Contains("Railroad Passage", StringComparison.Ordinal));

        Assert.True(flare.IsConditional);
        Assert.Contains(flare.Details, d => d.Contains("green flare", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Upstream currently attaches one Reserve switch id to every Customs extract (28 of 28), which
    /// is not real gating. The builder must drop it rather than label every exit "Activated by" and
    /// mark the whole map conditional. Building with no notes at all is the sharpest check: ZB-1013
    /// has no condition of its own here, so it must read as an ordinary extract. Its genuine gating
    /// survives through its wiki note, which <c>ExtractNotesTests</c> covers separately.
    /// </summary>
    [Fact]
    public void AMisassignedUpstreamSwitchIsNotReportedAsGating()
    {
        var store = LoadStore();
        var map = Catalog.Find("customs")!;
        var pois = PoiBuilder.Build(map, store.ForMap("customs")!, store);

        var zb = pois.Single(p => p.Name == "ZB-1013");

        Assert.DoesNotContain(zb.Details, d => d.StartsWith("Activated by", StringComparison.Ordinal));

        // The raw switch name is a scene path; it must not leak into the UI.
        Assert.DoesNotContain(zb.Details, d => d.Contains("DesignStuff", StringComparison.OrdinalIgnoreCase));
        Assert.False(zb.IsConditional);
    }

    /// <summary>
    /// The defensive guard: a switch id most of a map references is an upstream mis-assignment, so
    /// it must produce neither an "Activated by" line nor a conditional flag on any extract.
    /// </summary>
    [Fact]
    public void ASwitchReferencedByEveryExtractIsIgnoredAsMisassigned()
    {
        var store = LoadStore();
        var map = Catalog.Find("customs")!;

        var data = SyntheticMap(
            ("Extract A", ["bogus"]),
            ("Extract B", ["bogus"]),
            ("Extract C", ["bogus"]),
            ("Extract D", ["bogus"]));

        var pois = PoiBuilder.Build(map, data, store, notes: null);
        var extracts = pois.Where(p => p.IsExtract).ToArray();

        Assert.Equal(4, extracts.Length);
        Assert.All(extracts, e => Assert.False(e.IsConditional));
        Assert.All(extracts, e => Assert.DoesNotContain(
            e.Details, d => d.StartsWith("Activated by", StringComparison.Ordinal)));
    }

    /// <summary>A single switch on a single extract is real gating and must be left alone.</summary>
    [Fact]
    public void AnExtractWithItsOwnSwitchIsStillGated()
    {
        var store = LoadStore();
        var map = Catalog.Find("customs")!;

        var data = SyntheticMap(
            ("Gated Exit", ["only-one"]),
            ("Extract B", []),
            ("Extract C", []),
            ("Extract D", []));

        var pois = PoiBuilder.Build(map, data, store, notes: null);

        var gated = pois.Single(p => p.Name == "Gated Exit");
        Assert.True(gated.IsConditional);
        Assert.Contains(gated.Details, d => d.StartsWith("Activated by", StringComparison.Ordinal));

        var open = pois.Single(p => p.Name == "Extract B");
        Assert.False(open.IsConditional);
        Assert.Empty(open.Details);
    }

    /// <summary>
    /// Pins the minimum-map-size floor deliberately: on a two-extract map "half the extracts" is not
    /// meaningful, so a shared switch is still honoured rather than suppressed.
    /// </summary>
    [Fact]
    public void ATwoExtractMapIsTooSmallToTripTheGuard()
    {
        var store = LoadStore();
        var map = Catalog.Find("customs")!;

        var data = SyntheticMap(
            ("Extract A", ["shared"]),
            ("Extract B", ["shared"]));

        var pois = PoiBuilder.Build(map, data, store, notes: null);
        var extracts = pois.Where(p => p.IsExtract).ToArray();

        Assert.Equal(2, extracts.Length);
        Assert.All(extracts, e => Assert.True(e.IsConditional));
        Assert.All(extracts, e => Assert.Contains(
            e.Details, d => d.StartsWith("Activated by", StringComparison.Ordinal)));
    }

    [Fact]
    public void TransitsResolveTheirDestinationMap()
    {
        var store = LoadStore();
        var map = Catalog.Find("customs")!;
        var pois = PoiBuilder.Build(map, store.ForMap("customs")!, store);

        var transits = pois.Where(p => p.Kind == PoiKind.Transit).ToArray();

        Assert.Equal(4, transits.Length);
        Assert.Contains(transits, t => t.DestinationMap == "reserve");
        Assert.Contains(transits, t => t.DestinationMap == "shoreline");
        Assert.All(transits, t => Assert.Contains(t.Details, d => d.StartsWith("Leads to", StringComparison.Ordinal)));
    }

    [Fact]
    public void ExtractsProjectOntoTheMapTheyBelongTo()
    {
        var store = LoadStore();

        foreach (var name in new[] { "customs", "shoreline", "woods", "interchange" })
        {
            var map = Catalog.Find(name)!;
            var pois = PoiBuilder.Build(map, store.ForMap(name)!, store);
            var extracts = pois.Where(p => p.IsExtract).ToArray();

            if (extracts.Length == 0)
                continue;

            // Exits sit at the edges, so allow a margin, but they must not be off in space --
            // that is what a projection or bounds error looks like.
            var generous = map.BaseRect.Inflate(0.15);
            var stray = extracts.Where(e => !generous.Contains(e.Base)).ToArray();

            Assert.True(
                stray.Length == 0,
                $"{name}: {stray.Length} extracts outside the map, e.g. {stray.FirstOrDefault()?.Name}");
        }
    }

    [Fact]
    public void SpawnsAreClassifiedIntoTheHeatmapBands()
    {
        var store = LoadStore();
        var heatmap = new HeatmapOverlay { Map = Catalog.Find("customs") };
        heatmap.SetData(store.ForMap("customs"));

        // Customs has both PMC and scav starts, plus Reshala and his guards.
        Assert.True(heatmap.PointCount(SpawnGroup.Pmc) > 50);
        Assert.True(heatmap.PointCount(SpawnGroup.Scav) > 50);
        Assert.True(heatmap.PointCount(SpawnGroup.Boss) > 0);
    }

    [Fact]
    public void SwitchAndHazardNames_AreReadableRatherThanRawScenePaths()
    {
        var store = LoadStore();
        var map = Catalog.Find("woods")!;
        var pois = PoiBuilder.Build(map, store.ForMap("woods")!, store);

        foreach (var poi in pois.Where(p => p.Kind is PoiKind.Switch or PoiKind.Hazard))
        {
            Assert.DoesNotContain("/", poi.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("DesignStuff", poi.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void HitTest_PrefersAnExtractOverAMarkerSittingOnTopOfIt()
    {
        var map = Catalog.Find("customs")!;
        var viewport = new Viewport(map.BaseRect);
        viewport.Resize(1000, 800);
        viewport.FitAll();

        var position = new GamePosition(0, 0, 0);
        var basePoint = map.ToBase(position);

        var overlay = new PoiOverlay { Map = map };
        overlay.SetPois(
        [
            new MapPoi { Kind = PoiKind.LootContainer, Name = "Jacket", Position = position, Base = basePoint },
            new MapPoi { Kind = PoiKind.ExtractPmc, Name = "Exit", Position = position, Base = basePoint },
        ]);

        overlay.Visible[PoiKind.LootContainer] = true;
        overlay.Visible[PoiKind.ExtractPmc] = true;

        var screen = viewport.ToScreen(basePoint);
        var hit = overlay.HitTest(viewport, screen.X, screen.Y);

        Assert.Equal("Exit", hit?.Name);
    }

    [Fact]
    public void HitTest_IgnoresHiddenLayers()
    {
        var map = Catalog.Find("customs")!;
        var viewport = new Viewport(map.BaseRect);
        viewport.Resize(1000, 800);
        viewport.FitAll();

        var basePoint = map.ToBase(new GamePosition(0, 0, 0));
        var overlay = new PoiOverlay { Map = map };
        overlay.SetPois([new MapPoi
        {
            Kind = PoiKind.LootContainer,
            Name = "Jacket",
            Position = new GamePosition(0, 0, 0),
            Base = basePoint,
        }]);

        overlay.Visible[PoiKind.LootContainer] = false;

        var screen = viewport.ToScreen(basePoint);
        Assert.Null(overlay.HitTest(viewport, screen.X, screen.Y));
    }

    [Fact]
    public void ExtractLine_ReportsDistanceAndWhichWayToTurn()
    {
        var map = Catalog.Find("customs")!;

        var target = new MapPoi
        {
            Kind = PoiKind.ExtractPmc,
            Name = "Exit",
            // 100 m due +X of the player.
            Position = new GamePosition(100, 0, 0),
            Base = map.ToBase(new GamePosition(100, 0, 0)),
        };

        var overlay = new ExtractLineOverlay
        {
            Map = map,
            Target = target,
            PlayerPosition = new GamePosition(0, 0, 0),
            PlayerYawDegrees = 0, // facing +Z
        };

        Assert.Equal(100, overlay.DistanceMeters!.Value, 3);

        // +X is 90 degrees clockwise from +Z, so the exit is a right turn.
        Assert.Equal(90, overlay.RelativeBearingDegrees!.Value, 3);

        overlay.PlayerYawDegrees = 180;
        Assert.Equal(-90, overlay.RelativeBearingDegrees!.Value, 3);
    }

    [Fact]
    public void ExtractLine_HasNothingToReportWithoutAFix()
    {
        var overlay = new ExtractLineOverlay { Map = Catalog.Find("customs") };

        Assert.Null(overlay.DistanceMeters);
        Assert.Null(overlay.RelativeBearingDegrees);
    }
}
