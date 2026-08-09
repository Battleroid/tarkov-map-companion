using TarkovMapCompanion.Data;
using TarkovMapCompanion.Maps;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// What a row says under the exit's name, now that the detail pane is gone and the requirements
/// live in the list.
/// </summary>
public class ExitRowTests
{
    private static MapPoi Poi(
        PoiKind kind, string name, string? destination = null, params string[] details) => new()
    {
        Kind = kind,
        Name = name,
        Position = new GamePosition(0, 0, 0),
        Base = new MapPoint(0, 0),
        DestinationMap = destination,
        Details = details,
    };

    /// <summary>
    /// The name of a transit is already a complete sentence about where it goes. Repeating it
    /// underneath was the specific thing that made the old detail pane read as padding.
    /// </summary>
    [Fact]
    public void ATransitWhoseNameSaysWhereItGoesGetsNoSubtitle()
    {
        var poi = Poi(PoiKind.Transit, "Transit to Shoreline", destination: "shoreline");

        Assert.Equal("", poi.SubtitleLabel);
    }

    [Fact]
    public void ATransitWhoseNameDoesNotSayWhereItGoesStillGetsOne()
    {
        var poi = Poi(PoiKind.Transit, "Smugglers' Path", destination: "lighthouse");

        Assert.Equal("Leads to Lighthouse", poi.SubtitleLabel);
    }

    [Theory]
    [InlineData(PoiKind.ExtractPmc, "PMC only")]
    [InlineData(PoiKind.ExtractScav, "Scav only")]
    [InlineData(PoiKind.ExtractShared, "PMC and Scav")]
    public void AnExtractIsLabeledByWhoCanUseIt(PoiKind kind, string expected)
    {
        Assert.Equal(expected, Poi(kind, "RUAF Roadblock").SubtitleLabel);
    }

    [Fact]
    public void ARowWithNothingToAddReportsNoDetails()
    {
        var poi = Poi(PoiKind.Transit, "Transit to Shoreline", destination: "shoreline");

        Assert.False(poi.HasDetails);
        Assert.False(poi.ShowDetails);
    }

    [Fact]
    public void ConditionsCountAsDetailsEvenWithoutASubtitle()
    {
        var poi = Poi(PoiKind.Transit, "Transit to Shoreline", "shoreline", "Needs: 5000 Roubles");

        Assert.True(poi.HasDetails);
    }

    /// <summary>Expanding is what shows them; having them is not enough on its own.</summary>
    [Fact]
    public void DetailsOnlyShowOnceTheRowIsExpanded()
    {
        var poi = Poi(PoiKind.ExtractPmc, "Smugglers' Boat", null, "Needs: Note with code word Voron");

        Assert.True(poi.HasDetails);
        Assert.False(poi.ShowDetails);

        poi.DetailsExpanded = true;

        Assert.True(poi.ShowDetails);
    }

    /// <summary>Expanding a row that has nothing to say must not open an empty gap in the list.</summary>
    [Fact]
    public void ExpandingARowWithNothingToSayShowsNothing()
    {
        var poi = Poi(PoiKind.Transit, "Transit to Shoreline", destination: "shoreline");
        poi.DetailsExpanded = true;

        Assert.False(poi.ShowDetails);
    }
}
