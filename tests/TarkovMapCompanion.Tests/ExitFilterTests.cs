using TarkovMapCompanion.Data;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

public sealed class ExitFilterTests
{
    [Theory]
    [InlineData(ExitFilter.All, PoiKind.ExtractPmc, true)]
    [InlineData(ExitFilter.All, PoiKind.ExtractScav, true)]
    [InlineData(ExitFilter.All, PoiKind.Transit, true)]
    // Running as a PMC: your own exits, the shared ones, and transits.
    [InlineData(ExitFilter.AsPmc, PoiKind.ExtractPmc, true)]
    [InlineData(ExitFilter.AsPmc, PoiKind.ExtractShared, true)]
    [InlineData(ExitFilter.AsPmc, PoiKind.Transit, true)]
    [InlineData(ExitFilter.AsPmc, PoiKind.ExtractScav, false)]
    // Running as a Scav: the mirror image.
    [InlineData(ExitFilter.AsScav, PoiKind.ExtractScav, true)]
    [InlineData(ExitFilter.AsScav, PoiKind.ExtractShared, true)]
    [InlineData(ExitFilter.AsScav, PoiKind.ExtractPmc, false)]
    // The "only" variants are for planning, so they exclude even shared and transits.
    [InlineData(ExitFilter.PmcOnly, PoiKind.ExtractPmc, true)]
    [InlineData(ExitFilter.PmcOnly, PoiKind.ExtractShared, false)]
    [InlineData(ExitFilter.PmcOnly, PoiKind.Transit, false)]
    [InlineData(ExitFilter.ScavOnly, PoiKind.ExtractScav, true)]
    [InlineData(ExitFilter.ScavOnly, PoiKind.ExtractPmc, false)]
    [InlineData(ExitFilter.SharedOnly, PoiKind.ExtractShared, true)]
    [InlineData(ExitFilter.SharedOnly, PoiKind.ExtractPmc, false)]
    public void Includes_MatchesWhatEachFactionCanActuallyUse(ExitFilter filter, PoiKind kind, bool expected) =>
        Assert.Equal(expected, filter.Includes(kind));

    [Fact]
    public void EveryFilterHasAReadableLabel()
    {
        foreach (var filter in Enum.GetValues<ExitFilter>())
        {
            var label = filter.Label();

            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.NotEqual(filter.ToString(), label);
        }
    }

    [Fact]
    public void AsPmc_HidesTheScavOnlyExitsOnCustoms()
    {
        var catalog = MapCatalog.LoadEmbedded();
        var store = new MapDataStore(
            new AppSettings { AllowNetwork = false },
            cacheDirectory: Path.Combine(Path.GetTempPath(), "tmc-tests-nocache", Guid.NewGuid().ToString("N")));
        store.LoadLocal();

        var map = catalog.Find("customs")!;
        var pois = PoiBuilder.Build(map, store.ForMap("customs")!, store);
        var exits = pois.Where(p => p.IsExtract || p.Kind == PoiKind.Transit).ToArray();

        var asPmc = exits.Where(p => ExitFilter.AsPmc.Includes(p.Kind)).ToArray();

        // Customs has 16 Scav-only exits; a PMC should never see them.
        Assert.DoesNotContain(asPmc, p => p.Kind == PoiKind.ExtractScav);
        Assert.Contains(asPmc, p => p.Name == "Dorms V-Ex");
        Assert.Contains(asPmc, p => p.Name.Contains("Boiler Room Basement", StringComparison.Ordinal));
        Assert.True(asPmc.Length < exits.Length);
    }

    // ---- Sorting by distance ------------------------------------------------

    [Fact]
    public void SortingByDistance_OrdersExitsNearestFirst()
    {
        var catalog = MapCatalog.LoadEmbedded();
        var store = new MapDataStore(
            new AppSettings { AllowNetwork = false },
            cacheDirectory: Path.Combine(Path.GetTempPath(), "tmc-tests-nocache", Guid.NewGuid().ToString("N")));
        store.LoadLocal();

        var map = catalog.Find("customs")!;
        var pois = PoiBuilder.Build(map, store.ForMap("customs")!, store);
        var exits = pois.Where(p => p.IsExtract).ToArray();

        // Stand at Dorms and rank the exits from there.
        var player = new GamePosition(200, 0, 150);
        foreach (var exit in exits)
            exit.DistanceMeters = player.GroundDistanceTo(exit.Position);

        var ordered = exits.OrderBy(e => e.DistanceMeters ?? double.MaxValue).ToArray();

        // Monotonically increasing, and the nearest really is the nearest.
        for (var i = 1; i < ordered.Length; i++)
            Assert.True(ordered[i].DistanceMeters >= ordered[i - 1].DistanceMeters);

        Assert.Equal(exits.Min(e => e.DistanceMeters), ordered[0].DistanceMeters);
    }

    [Fact]
    public void DistanceLabel_IsEmptyUntilThePlayerHasBeenPlaced()
    {
        var poi = new MapPoi
        {
            Kind = PoiKind.ExtractPmc,
            Name = "Exit",
            Position = new GamePosition(0, 0, 0),
            Base = new MapPoint(0, 0),
        };

        Assert.Equal("", poi.DistanceLabel);

        poi.DistanceMeters = 348.4;
        Assert.Equal("348 m", poi.DistanceLabel);
    }

    // ---- Focus mode view restore --------------------------------------------

    [Fact]
    public void ViewportState_RoundTripsThroughCaptureAndRestore()
    {
        var viewport = new Viewport(new MapRect(0, 0, 1000, 800));
        viewport.Resize(600, 400);
        viewport.Scale = 2.5;
        viewport.Center = new MapPoint(320, 410);

        var saved = viewport.Capture();

        // Simulate focus mode taking the view over.
        viewport.FitToRect(new MapRect(10, 10, 60, 60), 0.15);
        Assert.NotEqual(saved.Scale, viewport.Scale);

        viewport.Restore(saved);

        Assert.Equal(saved.Scale, viewport.Scale, 9);
        Assert.Equal(saved.Center.X, viewport.Center.X, 9);
        Assert.Equal(saved.Center.Y, viewport.Center.Y, 9);
    }

    [Fact]
    public void Restore_AppliesScaleBeforeCentre_SoTheClampDoesNotEatThePosition()
    {
        // Restoring a zoomed-in centre while still at a zoomed-out scale would clamp the centre
        // toward the middle of the map and lose it. Order matters here.
        var viewport = new Viewport(new MapRect(0, 0, 1000, 800));
        viewport.Resize(600, 400);

        viewport.Scale = 8;
        viewport.Center = new MapPoint(990, 790);
        var saved = viewport.Capture();

        viewport.FitAll();
        viewport.Restore(saved);

        Assert.Equal(saved.Center.X, viewport.Center.X, 6);
        Assert.Equal(saved.Center.Y, viewport.Center.Y, 6);
    }
}
