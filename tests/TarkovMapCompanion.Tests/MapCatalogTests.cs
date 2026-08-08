using TarkovMapCompanion.Maps;
using Xunit;

namespace TarkovMapCompanion.Tests;

public sealed class MapCatalogTests
{
    private static readonly MapCatalog Catalog = MapCatalog.LoadEmbedded();

    [Fact]
    public void LoadEmbedded_FindsTheThirteenInteractiveMaps()
    {
        var names = Catalog.Maps.Select(m => m.NormalizedName).ToArray();

        Assert.Equal(13, names.Length);
        Assert.Contains("customs", names);
        Assert.Contains("shoreline", names);
        Assert.Contains("streets-of-tarkov", names);
        Assert.Contains("the-labyrinth", names);
        Assert.Contains("terminal", names);
        Assert.Contains("icebreaker", names);
    }

    [Fact]
    public void LoadEmbedded_SkipsTheNonInteractive2DAnd3DVariants()
    {
        // Each group also lists e.g. "customs-2d" / "customs-3d", which we have no renderer for.
        Assert.DoesNotContain(Catalog.Maps, m => m.Key.EndsWith("-2d", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Catalog.Maps, m => m.Key.EndsWith("-3d", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryMap_HasImageryWeCanActuallyRender()
    {
        foreach (var map in Catalog.Maps)
            Assert.True(map.HasSvg || map.HasTiles, $"{map.NormalizedName} has neither an SVG nor a tile pyramid");
    }

    [Fact]
    public void ImagerySplit_IsTheOneWeDesignedTheTwoRenderersAround()
    {
        var svgOnly = Catalog.Maps.Where(m => m.HasSvg && !m.HasTiles).Select(m => m.NormalizedName).Order().ToArray();
        var tilesOnly = Catalog.Maps.Where(m => m.HasTiles && !m.HasSvg).Select(m => m.NormalizedName).Order().ToArray();

        // Neither renderer covers everything on its own, which is why the app ships both.
        // If this ever collapses to one list, one of them has become dead weight.
        Assert.Equal(["lighthouse", "streets-of-tarkov", "terminal"], svgOnly);
        Assert.Equal(["icebreaker", "the-lab", "the-labyrinth"], tilesOnly);

        // The remaining seven have both; SVG is preferred there for its crispness at high zoom.
        Assert.Equal(7, Catalog.Maps.Count(m => m is { HasSvg: true, HasTiles: true }));
    }

    [Fact]
    public void Shoreline_HasTheGeometryTheProjectionTestsAssume()
    {
        var shoreline = Catalog.Find("shoreline");

        Assert.NotNull(shoreline);
        Assert.Equal(180, shoreline!.Projection.CoordinateRotationDegrees);
        Assert.Equal(0.16, shoreline.Projection.ScaleX, 6);
        Assert.Equal(1560 * 0.16, shoreline.BaseRect.Width, 6);
    }

    [Fact]
    public void Reserve_UsesItsSeparateSvgBoundsForTheOverlay()
    {
        // Reserve is the only map where svgBounds differs from bounds. Collapsing the two puts
        // the whole overlay about 20 game meters off.
        var reserve = Catalog.Find("reserve");

        Assert.NotNull(reserve);
        Assert.NotEqual(reserve!.BaseRect, reserve.SvgBaseRect);
    }

    [Fact]
    public void EveryOtherMap_HasItsSvgRectEqualToItsBoundsRect()
    {
        foreach (var map in Catalog.Maps.Where(m => m.NormalizedName != "reserve"))
            Assert.Equal(map.BaseRect, map.SvgBaseRect);
    }

    [Fact]
    public void TileSize_DefaultsTo256ButHonorsTheLabsOverride()
    {
        Assert.Equal(175, Catalog.Find("the-lab")!.TileSize);
        Assert.Equal(256, Catalog.Find("shoreline")!.TileSize);
    }

    [Fact]
    public void RotationsAreTheThreeDistinctValuesWeHandle()
    {
        var rotations = Catalog.Maps.Select(m => m.Projection.CoordinateRotationDegrees).Distinct().Order().ToArray();

        Assert.Equal([90d, 180d, 270d], rotations);
    }

    [Fact]
    public void Customs_ParsesItsFloorsAndTheirNamedFootprints()
    {
        var customs = Catalog.Find("customs")!;
        var secondFloor = customs.Floors.Single(f => f.Name == "2nd Floor");

        Assert.NotNull(secondFloor.TilePathTemplate);
        Assert.Equal("Second_Floor", secondFloor.SvgLayerId);
        Assert.False(secondFloor.ShownByDefault);

        // The heterogeneous [[x,z],[x,z],"label"] entries must survive the custom converter.
        var footprints = secondFloor.Extents.SelectMany(e => e.Bounds ?? []).ToArray();
        Assert.Contains(footprints, f => f.Name == "dorms");
    }

    [Fact]
    public void MapFloor_Covers_RespectsBothHeightAndFootprint()
    {
        var customs = Catalog.Find("customs")!;
        var secondFloor = customs.Floors.Single(f => f.Name == "2nd Floor");

        // Dorms footprint is [[243, 190], [165, 125]] over height 2.7 to 6.5.
        Assert.True(secondFloor.Covers(new GamePosition(200, 4.0, 160)));

        // Right place, wrong height.
        Assert.False(secondFloor.Covers(new GamePosition(200, 40.0, 160)));

        // Right height, nowhere near any of its footprints.
        Assert.False(secondFloor.Covers(new GamePosition(-9000, 4.0, -9000)));
    }

    [Fact]
    public void ContainsPosition_AcceptsRealShorelineFixesAndRejectsFarawayOnes()
    {
        var shoreline = Catalog.Find("shoreline")!;

        // Straight out of Casey's screenshot folder.
        Assert.True(shoreline.ContainsPosition(new GamePosition(-720.10, -48.62, 430.51)));
        Assert.True(shoreline.ContainsPosition(new GamePosition(444.22, -51.93, -13.60)));

        Assert.False(shoreline.ContainsPosition(new GamePosition(5000, 0, 5000)));
    }

    [Fact]
    public void Resolve_FallsBackInsteadOfReturningNullForAnUnknownName()
    {
        Assert.NotNull(Catalog.Resolve("a-map-that-does-not-exist"));
        Assert.Equal("shoreline", Catalog.Resolve("SHORELINE").NormalizedName);
    }

    [Fact]
    public void Parse_SkipsAnUnprojectableMapRatherThanFailingTheWholeCatalog()
    {
        var catalog = MapCatalog.Parse("""
            [
              { "normalizedName": "broken",
                "maps": [ { "key": "broken", "projection": "interactive", "coordinateRotation": 180 } ] },
              { "normalizedName": "fine",
                "maps": [ { "key": "fine", "projection": "interactive", "coordinateRotation": 180,
                            "transform": [1, 0, 1, 0], "bounds": [[10, -10], [-10, 10]],
                            "svgPath": "https://example.invalid/fine.svg" } ] }
            ]
            """);

        Assert.Single(catalog.Maps);
        Assert.Equal("fine", catalog.Maps[0].NormalizedName);
    }

    [Fact]
    public void Parse_WithNoUsableMaps_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => MapCatalog.Parse("""[{"normalizedName":"x","maps":[]}]"""));
    }

    [Theory]
    [InlineData("streets-of-tarkov", "Streets of Tarkov")]
    [InlineData("ground-zero", "Ground Zero")]
    [InlineData("the-lab", "The Lab")]
    [InlineData("customs", "Customs")]
    public void DisplayName_ReadsLikeAPlaceName(string normalized, string expected) =>
        Assert.Equal(expected, GameMap.ToDisplayName(normalized));

    [Fact]
    public void EveryMap_CarriesAttributionOrIsExplicitlyUnattributed()
    {
        // The About screen credits these; a silent null would drop a required credit.
        var attributed = Catalog.Maps.Count(m => !string.IsNullOrWhiteSpace(m.Author));

        Assert.True(attributed >= 10, $"only {attributed} of {Catalog.Maps.Count} maps name an author");
    }
}
