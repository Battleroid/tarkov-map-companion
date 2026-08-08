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

    /// <summary>
    /// Verbatim reader output for a PMC raid on Customs: seven exits and four transits.
    /// </summary>
    /// <remarks>
    /// A PMC's rows are numbered EXFILnn where a Scav's are EXITnn. Reading EXFIL as "the exit
    /// being used right now" -- which is how it looks in the extracting banner -- threw away every
    /// row of every PMC list, leaving only the transits.
    /// </remarks>
    private static IReadOnlyList<OcrLine> RealPmcPanel() =>
    [
        Line("t, Find", 1841, 30, 91, 15),
        Line("an extraction point", 1946, 30, 310, 20),
        Line($"WEXFIL{SlashedZero}I", 1789, 103, 102, 24),
        Line($"ZB-{SlashedZero}13", 1948, 106, 120, 22),
        Line($"EXFIL{SlashedZero}2", 1814, 172, 107, 25),
        Line("Dorms V-Ex", 1954, 175, 140, 19),
        Line($"EXFIL{SlashedZero}3", 1814, 242, 107, 24),
        Line("Crossroads", 1953, 245, 140, 18),
        Line($"EXFIL{SlashedZero}4 Trailer Park", 1814, 311, 300, 24),
        Line($"EXFIL{SlashedZero}5 RUAF Roadblock", 1814, 380, 330, 24),
        Line($"EXFIL{SlashedZero}6 Railroad Passage (Flare)", 1814, 450, 420, 24),
        Line($"EXFIL{SlashedZero}7 Boiler Room Basement (Co-op)", 1814, 519, 470, 24),
        Line($"TRANSIT{SlashedZero}I Transit to Reserve", 1812, 592, 387, 24),
        Line($"TRANSIT{SlashedZero}2 Transit to Factory", 1812, 668, 391, 24),
        Line($"TRANSIT{SlashedZero}3 Transit to Interchange", 1812, 744, 441, 24),
        Line($"TRANSIT{SlashedZero}3 Transit Co Shoreline", 1812, 818, 406, 24),
    ];

    [Fact]
    public void ReadsAPmcListWhereEveryRowIsNumberedExfil()
    {
        var reading = ExtractPanelParser.Parse(RealPmcPanel());

        Assert.True(reading.PanelFound);

        // Six ids strip cleanly; the seventh picked up a stray glyph and is recovered through its
        // alternative readings instead, which is why the assertion is on candidates rather than
        // on the row kind.
        Assert.Equal(6, reading.Rows.Count(r => r.Kind == PanelRowKind.Extract));
        Assert.Equal(4, reading.Rows.Count(r => r.Kind == PanelRowKind.Transit));

        string[] expected =
        [
            $"ZB-{SlashedZero}13", "Dorms V-Ex", "Crossroads", "Trailer Park", "RUAF Roadblock",
            "Railroad Passage (Flare)", "Boiler Room Basement (Co-op)",
        ];

        foreach (var name in expected)
            Assert.Contains(reading.Exits, r => r.NameCandidates.Contains(name));
    }

    [Fact]
    public void RecoversARowWhoseIdPickedUpAStrayGlyph()
    {
        // The first row read as "WEXFIL01 ZB-013". Judged on its left edge alone it falls outside
        // the panel and the whole row disappears.
        var reading = ExtractPanelParser.Parse(RealPmcPanel());

        var row = reading.Rows.Single(r => r.RawText.Contains("ZB-", StringComparison.Ordinal));

        Assert.Contains($"ZB-{SlashedZero}13", row.NameCandidates);
    }

    [Fact]
    public void TheExtractingBannerIsNotTheListEvenWithSeveralRows()
    {
        // Rows under the "Stay" banner are numbered exactly like the list's, so only the wording
        // separates them. Getting this wrong dims every exit at the worst possible moment.
        var lines = new List<OcrLine>
        {
            Line("Stay in the extraction point", 1700, 30, 400, 20),
            Line($"EXFIL{SlashedZero}5 RUAF Roadblock", 1812, 103, 319, 24),
            Line($"EXFIL{SlashedZero}6 Crossroads", 1812, 172, 300, 24),
        };

        Assert.False(ExtractPanelParser.Parse(lines).PanelFound);
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

    [Theory]
    [InlineData("0:00:54")]
    [InlineData("Ø:ØØ:54")]
    // Smaller text makes the reader return the slashed zero in lowercase. A digit class carrying
    // only the uppercase form left the countdown attached to the name, dropped it below the match
    // floor, and made a perfectly legible row look like a resolution limit.
    [InlineData("ø:øø:54")]
    [InlineData("0.00.54")]
    public void StripsTheCountdownHoweverTheZerosAreRead(string timer)
    {
        var lines = new List<OcrLine>
        {
            Line("Find an extraction point", 1841, 35, 400, 20),
            Line($"TRANSIT{SlashedZero}I Transit to Reserve {timer}", 1812, 384, 500, 24),
            Line($"TRANSIT{SlashedZero}2 Transit to Factory {timer}", 1812, 460, 500, 24),
        };

        var reading = ExtractPanelParser.Parse(lines);

        Assert.Equal(
            ["Transit to Reserve", "Transit to Factory"],
            reading.Rows.Where(r => r.Kind == PanelRowKind.Transit).Select(r => r.Name));
    }

    [Fact]
    public void DoesNotMistakePartOfANameForACountdown()
    {
        var lines = new List<OcrLine>
        {
            Line("Find an extraction point", 1841, 35, 400, 20),
            Line($"EXIT{SlashedZero}I Warehouse 17", 1814, 103, 300, 24),
            Line($"EXIT{SlashedZero}2 Smugglers' Bunker (ZB-1012)", 1814, 172, 400, 24),
        };

        var reading = ExtractPanelParser.Parse(lines);

        Assert.Equal(
            ["Warehouse 17", "Smugglers' Bunker (ZB-1012)"],
            reading.Rows.Where(r => r.Kind == PanelRowKind.Extract).Select(r => r.Name));
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

    [Theory]
    // Verbatim from a 1280x720 frame, where the id column degrades in ways no pattern anticipates.
    [InlineData("EXIT u Boiler Room Basement (Co-op)", "Boiler Room Basement (Co-op)")]
    [InlineData("TRANSIT Q Transit to Factory", "Transit to Factory")]
    [InlineData("TRANSITM Transit to Reserve", "Transit to Reserve")]
    [InlineData("TRANSITU Transit to Shoreline", "Transit to Shoreline")]
    [InlineData("EXITØI Sniper Roadblock", "Sniper Roadblock")]
    public void OffersAReadingWithTheIdRemovedHoweverItWasMangled(string raw, string expected)
    {
        var lines = new List<OcrLine>
        {
            Line("Find an extraction point", 1841, 35, 400, 20),
            Line(raw, 1812, 384, 500, 24),
            Line($"EXIT{SlashedZero}9 Crossroads", 1812, 460, 300, 24),
        };

        var row = ExtractPanelParser.Parse(lines).Rows.Single(r => r.RawText == raw);

        Assert.Contains(expected, row.NameCandidates);
    }

    [Fact]
    public void KeepsTheWholeNameWhenARowHasNoIdAtAll()
    {
        // "Transit to Reserve" begins with the same word as the id column. The full reading has to
        // survive, or the row degrades to "to Reserve" and matches nothing.
        var lines = new List<OcrLine>
        {
            Line("Find an extraction point", 1841, 35, 400, 20),
            Line("Transit to Reserve", 1812, 384, 387, 24),
            Line($"EXIT{SlashedZero}I Sniper Roadblock", 1814, 103, 300, 24),
        };

        var row = ExtractPanelParser.Parse(lines).Rows.Single(r => r.RawText == "Transit to Reserve");

        Assert.Equal("Transit to Reserve", row.Name);
        Assert.Contains("Transit to Reserve", row.NameCandidates);
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
