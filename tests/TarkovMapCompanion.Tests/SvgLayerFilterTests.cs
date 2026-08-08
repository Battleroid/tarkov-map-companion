using TarkovMapCompanion.Rendering;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// The map documents stack every floor as opaque geometry, so which groups survive this filter is
/// the whole of floor switching. Getting it wrong shows the wrong level, or a blank map.
/// </summary>
public sealed class SvgLayerFilterTests
{
    /// <summary>Shaped like the real maps: shared style/defs, a base group, and floor groups.</summary>
    private const string Svg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
          <style id="style_common">.a{fill:#000}</style>
          <defs id="defs1"></defs>
          <g id="Ground_Floor"><rect id="ground-rect" width="10" height="10"/></g>
          <g id="First_Floor" data-keep-with-group="Ground_Floor"><rect id="first-rect"/></g>
          <g id="Second_Floor"><rect id="second-rect"/></g>
          <g id="Basement"><rect id="basement-rect"/></g>
          <g><rect id="unnamed-group-rect"/></g>
        </svg>
        """;

    [Fact]
    public void ListLayerGroups_FindsOnlyTopLevelGroupsWithIds()
    {
        var groups = SvgLayerFilter.ListLayerGroups(Svg);

        Assert.Equal(["Ground_Floor", "First_Floor", "Second_Floor", "Basement"], groups);
    }

    [Fact]
    public void BaseOnly_KeepsTheGroundFloorAndAnythingPinnedToIt()
    {
        var result = SvgLayerFilter.Filter(Svg, "Ground_Floor", []);

        Assert.Contains("ground-rect", result, StringComparison.Ordinal);
        Assert.Contains("first-rect", result, StringComparison.Ordinal);   // data-keep-with-group
        Assert.DoesNotContain("second-rect", result, StringComparison.Ordinal);
        Assert.DoesNotContain("basement-rect", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedDefinitionsAreNeverDropped()
    {
        // Losing the stylesheet renders the map as untextured black shapes.
        var result = SvgLayerFilter.Filter(Svg, "Ground_Floor", ["Basement"]);

        Assert.Contains("style_common", result, StringComparison.Ordinal);
        Assert.Contains("defs1", result, StringComparison.Ordinal);
    }

    [Fact]
    public void HidingTheBase_IsWhatMakesAnUndergroundFloorVisible()
    {
        // The Factory Tunnels case: the basement is drawn beneath opaque ground geometry, so it
        // only becomes visible once the ground floor is dropped.
        var result = SvgLayerFilter.Filter(Svg, "Ground_Floor", ["Basement"], includeBase: false);

        Assert.Contains("basement-rect", result, StringComparison.Ordinal);
        Assert.DoesNotContain("ground-rect", result, StringComparison.Ordinal);

        // Anything pinned to the base goes with it.
        Assert.DoesNotContain("first-rect", result, StringComparison.Ordinal);
    }

    [Fact]
    public void HidingEverything_ProducesAnEmptyMapRatherThanSilentlyRestoringTheGround()
    {
        // Deliberately asking for nothing is a real state to pass through while switching floors.
        // Redrawing the ground floor anyway would make the checkbox look broken.
        var result = SvgLayerFilter.Filter(Svg, "Ground_Floor", [], includeBase: false);

        Assert.DoesNotContain("ground-rect", result, StringComparison.Ordinal);
        Assert.DoesNotContain("basement-rect", result, StringComparison.Ordinal);
        Assert.Contains("style_common", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnrecognizedBaseFallsBackToTheWholeDocument()
    {
        // Showing everything beats showing nothing when the data does not match the document.
        var result = SvgLayerFilter.Filter(Svg, "No_Such_Group", []);

        Assert.Contains("ground-rect", result, StringComparison.Ordinal);
        Assert.Contains("basement-rect", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralFloorsCanBeShownAtOnce()
    {
        var result = SvgLayerFilter.Filter(Svg, "Ground_Floor", ["Second_Floor", "Basement"]);

        Assert.Contains("ground-rect", result, StringComparison.Ordinal);
        Assert.Contains("second-rect", result, StringComparison.Ordinal);
        Assert.Contains("basement-rect", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedSvgIsReturnedUnchangedInsteadOfThrowing()
    {
        const string broken = "<svg><g id='oops'>";

        Assert.Equal(broken, SvgLayerFilter.Filter(broken, "oops", []));
        Assert.Empty(SvgLayerFilter.ListLayerGroups(broken));
    }
}
