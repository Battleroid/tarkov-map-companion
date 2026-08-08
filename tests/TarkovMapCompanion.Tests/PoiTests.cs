using TarkovMapCompanion.Data;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Exercises the POI pipeline against the snapshot that actually ships, so a bad regeneration or
/// an upstream shape change fails here rather than showing up as an empty map.
/// </summary>
public sealed class PoiTests
{
    private static readonly MapCatalog Catalog = MapCatalog.LoadEmbedded();

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
        Assert.Equal(27, extracts.Length);

        // Localization keys must have been resolved; a raw EXFIL_ key means the merge broke.
        Assert.DoesNotContain(extracts, e => e.Name.StartsWith("EXFIL", StringComparison.Ordinal));

        Assert.Contains(extracts, e => e.Name == "Dorms V-Ex" && e.Kind == PoiKind.ExtractPmc);
        Assert.Contains(extracts, e => e.Name == "Scav Checkpoint" && e.Kind == PoiKind.ExtractScav);
        Assert.Contains(extracts, e => e.Name == "Crossroads" && e.Kind == PoiKind.ExtractShared);
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

    [Fact]
    public void AnExtractGatedByASwitch_SaysSo()
    {
        var store = LoadStore();
        var map = Catalog.Find("customs")!;
        var pois = PoiBuilder.Build(map, store.ForMap("customs")!, store);

        var zb013 = pois.Single(p => p.Name == "ZB-013");

        Assert.Contains(zb013.Details, d => d.StartsWith("Activated by", StringComparison.Ordinal));

        // The raw switch name is a scene path; it must not leak into the UI.
        Assert.DoesNotContain(zb013.Details, d => d.Contains("DesignStuff", StringComparison.OrdinalIgnoreCase));
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
