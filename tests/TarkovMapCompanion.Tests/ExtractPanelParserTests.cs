using TarkovMapCompanion.Vision;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Parsing Tarkov's extraction panel out of recognized text.
/// </summary>
/// <remarks>
/// The canned lines in <see cref="RealCustomsPanel"/> are not invented. They are exactly what the
/// Windows OCR engine returned for a real 2560x1440 screenshot of a Customs raid, character errors
/// and all, so these tests fail if the parser stops coping with the reader we actually ship.
/// </remarks>
public sealed class ExtractPanelParserTests
{
    // Tarkov's font uses a slashed zero, which the reader reports as this character.
    private const string SlashedZero = "Ø";

    private static OcrLine Line(string text, double x, double y, double width, double height) =>
        new(text, new TextBox(x, y, width, height));

    /// <summary>
    /// Verbatim reader output for a screenshot with the exit list open: four exits and four
    /// transits. Note the header arriving as two fragments, the id column split from the name for
    /// EXIT rows but merged for TRANSIT rows, and "RUAF" read as "RIJAF".
    /// </summary>
    private static IReadOnlyList<OcrLine> RealCustomsPanel() =>
    [
        Line("t, Find", 1841, 35, 91, 15),
        Line("an extraction point", 1946, 30, 310, 20),
        Line($"EXIT{SlashedZero}I", 1814, 103, 102, 24),
        Line("Sniper Roadblock", 1948, 106, 193, 22),
        Line($"EXIT{SlashedZero}2", 1814, 172, 107, 25),
        Line("RIJAF Roadblock", 1949, 175, 178, 19),
        Line($"EXIT{SlashedZero}3", 1814, 242, 107, 24),
        Line("Warehouse 17", 1948, 245, 156, 18),
        Line($"EXIT{SlashedZero}4", 1814, 311, 108, 24),
        Line("Boiler Room Basement (Co-op)", 1949, 314, 340, 18),
        Line($"TRANSIT{SlashedZero}I Transit to Reserve", 1812, 384, 387, 24),
        Line($"TRANSIT{SlashedZero}2 Transit to Factory", 1812, 460, 391, 24),
        Line($"TRANSIT{SlashedZero}3 Transit to Interchange", 1812, 536, 441, 24),
        Line($"TRANSIT{SlashedZero}3 Transit to Shoreline", 1812, 612, 406, 24),
    ];

    [Fact]
    public void ReadsEveryRowOfARealPanel()
    {
        var reading = ExtractPanelParser.Parse(RealCustomsPanel());

        Assert.True(reading.PanelFound);

        Assert.Equal(
            ["Sniper Roadblock", "RIJAF Roadblock", "Warehouse 17", "Boiler Room Basement (Co-op)"],
            reading.Rows.Where(r => r.Kind == PanelRowKind.Extract).Select(r => r.Name));

        Assert.Equal(
            ["Transit to Reserve", "Transit to Factory", "Transit to Interchange", "Transit to Shoreline"],
            reading.Rows.Where(r => r.Kind == PanelRowKind.Transit).Select(r => r.Name));
    }

    [Fact]
    public void JoinsAHeaderTheReaderSplitInTwo()
    {
        var reading = ExtractPanelParser.Parse(RealCustomsPanel());

        var header = Assert.Single(reading.Rows, r => r.Kind == PanelRowKind.ListHeader);
        Assert.Contains("extraction point", header.RawText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PairsTheIdColumnWithTheNameBesideIt()
    {
        // The id is set in a taller, heavier face than the name, so the two never share a baseline
        // and can only be paired by the band they sit in.
        var reading = ExtractPanelParser.Parse(RealCustomsPanel());

        Assert.Contains(reading.Rows, r => r.Name == "Sniper Roadblock" && r.Kind == PanelRowKind.Extract);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(11)]
    public void HandlesWhateverNumberOfExitsTheRaidHas(int count)
    {
        // The list is different every raid, so nothing may assume a row count.
        var lines = new List<OcrLine> { Line("Find an extraction point", 1841, 35, 400, 20) };

        for (var i = 0; i < count; i++)
        {
            var y = 100 + (i * 70);
            lines.Add(Line($"EXIT{SlashedZero}{i + 1}", 1814, y, 102, 24));
            lines.Add(Line($"Exit Number {i + 1}", 1948, y + 3, 193, 22));
        }

        var reading = ExtractPanelParser.Parse(lines);

        Assert.True(reading.PanelFound);
        Assert.Equal(count, reading.Rows.Count(r => r.Kind == PanelRowKind.Extract));
    }

    [Fact]
    public void ARaidWithNoTransitsIsFine()
    {
        var lines = new List<OcrLine>
        {
            Line("Find an extraction point", 1841, 35, 400, 20),
            Line($"EXIT{SlashedZero}I", 1814, 103, 102, 24),
            Line("Sniper Roadblock", 1948, 106, 193, 22),
            Line($"EXIT{SlashedZero}2", 1814, 172, 107, 25),
            Line("Warehouse 17", 1948, 175, 156, 18),
        };

        var reading = ExtractPanelParser.Parse(lines);

        Assert.True(reading.PanelFound);
        Assert.DoesNotContain(reading.Rows, r => r.Kind == PanelRowKind.Transit);
        Assert.Equal(2, reading.Exits.Count);
    }

    [Fact]
    public void TheActiveExtractionBannerIsNotTheExitList()
    {
        // Standing in an extract shows one EXFIL row and no list. Treating that as "these are your
        // exits" would dim every other exit on the map at the worst possible moment.
        var lines = new List<OcrLine>
        {
            Line($"EXFIL{SlashedZero}5 RUAF Roadblock", 1700, 78, 319, 37),
        };

        var reading = ExtractPanelParser.Parse(lines);

        Assert.False(reading.PanelFound);
    }

    [Fact]
    public void TheStayInTheExtractionPointBannerIsNotTheExitList()
    {
        var lines = new List<OcrLine>
        {
            Line("Stay in the extraction point", 1700, 30, 400, 20),
            Line($"EXFIL{SlashedZero}5 RUAF Roadblock", 1700, 78, 319, 37),
        };

        var reading = ExtractPanelParser.Parse(lines);

        Assert.False(reading.PanelFound);
    }

    [Fact]
    public void ReportsTheExitBeingUsedWhenTheListIsAlsoOpen()
    {
        var lines = new List<OcrLine>
        {
            Line("Find an extraction point", 1841, 35, 400, 20),
            Line($"EXIT{SlashedZero}I", 1814, 103, 102, 24),
            Line("Sniper Roadblock", 1948, 106, 193, 22),
            Line($"EXIT{SlashedZero}2", 1814, 172, 107, 25),
            Line("Warehouse 17", 1948, 175, 156, 18),
            Line($"EXFIL{SlashedZero}5 RUAF Roadblock", 1812, 250, 319, 24),
        };

        var reading = ExtractPanelParser.Parse(lines);

        Assert.True(reading.PanelFound);
        Assert.Equal("RUAF Roadblock", reading.ActiveExtractName);
    }

    [Fact]
    public void IgnoresHudTextToTheLeftOfThePanel()
    {
        // The hotbar shares the top of the frame and sits in the same vertical band as the first
        // row. Merged in, it would corrupt that row's name and lose a real exit.
        var lines = RealCustomsPanel().ToList();
        lines.Add(Line("Compass", 1700, 108, 63, 14));
        lines.Add(Line("R1500", 1600, 103, 51, 16));

        var reading = ExtractPanelParser.Parse(lines);

        Assert.True(reading.PanelFound);
        Assert.Contains(reading.Rows, r => r.Name == "Sniper Roadblock");
        Assert.DoesNotContain(reading.Rows, r => r.RawText.Contains("Compass", StringComparison.Ordinal));
    }

    [Fact]
    public void StripsTheCountdownFromATransitRow()
    {
        var lines = new List<OcrLine>
        {
            Line("Find an extraction point", 1841, 35, 400, 20),
            Line($"TRANSIT{SlashedZero}I Transit to Reserve 0:00:54", 1812, 384, 500, 24),
            Line($"TRANSIT{SlashedZero}2 Transit to Factory 0:00:54", 1812, 460, 500, 24),
        };

        var reading = ExtractPanelParser.Parse(lines);

        Assert.Equal(
            ["Transit to Reserve", "Transit to Factory"],
            reading.Rows.Where(r => r.Kind == PanelRowKind.Transit).Select(r => r.Name));
    }

    [Fact]
    public void DoesNotEatTheFirstWordOfAnUnprefixedTransitRow()
    {
        // "TRANSIT" is also how these names begin. Stripping it as an id would leave "to Reserve",
        // which matches nothing.
        var lines = new List<OcrLine>
        {
            Line("Find an extraction point", 1841, 35, 400, 20),
            Line("Transit to Reserve", 1812, 384, 387, 24),
            Line($"EXIT{SlashedZero}I", 1814, 103, 102, 24),
            Line("Sniper Roadblock", 1948, 106, 193, 22),
        };

        var reading = ExtractPanelParser.Parse(lines);

        Assert.Contains(reading.Rows, r => r.Name == "Transit to Reserve");
    }

    [Fact]
    public void NoTextAtAllIsNotAPanel()
    {
        Assert.False(ExtractPanelParser.Parse([]).PanelFound);
    }

    [Fact]
    public void AnOrdinaryScreenshotIsNotAPanel()
    {
        // Most screenshots are just screenshots.
        var lines = new List<OcrLine>
        {
            Line("R1500", 1600, 103, 51, 16),
            Line("Compass", 1700, 108, 63, 14),
            Line("MP-133", 1082, 108, 51, 11),
        };

        Assert.False(ExtractPanelParser.Parse(lines).PanelFound);
    }
}
