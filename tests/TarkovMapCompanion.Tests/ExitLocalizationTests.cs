using TarkovMapCompanion.Data;
using TarkovMapCompanion.Localization;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// The display-only localization added on top of the English source of truth: the wiki exit
/// conditions gain a Chinese rendering shown in place of the English one, and the floor toggles
/// get translated labels without the layer keys underneath them changing.
/// </summary>
public sealed class ExitLocalizationTests
{
    private static readonly MapCatalog Catalog = MapCatalog.LoadEmbedded();

    private static ExtractNotesStore LoadNotes()
    {
        var notes = new ExtractNotesStore();
        notes.Load();
        return notes;
    }

    private static MapDataStore LoadMapData()
    {
        var store = new MapDataStore(
            new AppSettings { AllowNetwork = false },
            cacheDirectory: Path.Combine(Path.GetTempPath(), "tmc-exitloc-tests", Guid.NewGuid().ToString("N")));

        store.LoadLocal();
        return store;
    }

    // ---- Chinese exit conditions -----------------------------------------------------------

    [Fact]
    public void TheChineseOverlayIsEmbeddedAndLoads()
    {
        var note = LoadNotes().Find("customs", "Dorms V-Ex");

        Assert.NotNull(note);
        Assert.False(string.IsNullOrWhiteSpace(note!.RequirementZh));
        Assert.Contains("卢布", note.RequirementZh!);
    }

    [Fact]
    public void TheOverlayDoesNotClobberTheEnglishFields()
    {
        var note = LoadNotes().Find("customs", "Dorms V-Ex")!;

        // The English requirement is still what the English build and the tests key off.
        Assert.Contains("Roubles", note.Requirement!, StringComparison.OrdinalIgnoreCase);
        Assert.False(note.AlwaysAvailable);
    }

    [Fact]
    public void AChineseRequirementProducesAChineseDetailAndKeepsTheEnglishOne()
    {
        var store = LoadMapData();
        var map = Catalog.Find("customs")!;
        var pois = PoiBuilder.Build(map, store.ForMap("customs")!, store, LoadNotes());

        var dorms = pois.Single(p => p.Name == "Dorms V-Ex");

        Assert.Contains(dorms.Details, d => d.Contains("Roubles", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dorms.DetailsZh, d => d.Contains("卢布", StringComparison.Ordinal));

        // Parallel, index for index, so the display can pair them up.
        Assert.Equal(dorms.Details.Count, dorms.DetailsZh.Count);
    }

    /// <summary>
    /// The English list is what the bilingual echo draws, so its wrapper has to be English too --
    /// not the active language's text sitting in front of an English body.
    /// </summary>
    /// <remarks>
    /// Only observable while Chinese is active: in English <c>Loc</c> and <c>LocEnglish</c> agree,
    /// which is how the original "需要：&lt;English&gt;" echo slipped past the other tests here.
    /// </remarks>
    [Fact]
    public void TheEnglishEchoKeepsAnEnglishWrapperUnderChinese()
    {
        var store = LoadMapData();
        var map = Catalog.Find("customs")!;
        var notes = LoadNotes();

        var previous = Localizer.Language;
        Localizer.SetLanguage(AppLanguage.ChineseSimplified);

        try
        {
            var pois = PoiBuilder.Build(map, store.ForMap("customs")!, store, notes);
            var dorms = pois.Single(p => p.Name == "Dorms V-Ex");

            Assert.Contains(dorms.Details, d => d.StartsWith("Needs:", StringComparison.Ordinal));
            Assert.DoesNotContain(dorms.Details, d => d.StartsWith("需要", StringComparison.Ordinal));

            // The Chinese list keeps the Chinese wrapper.
            Assert.Contains(dorms.DetailsZh, d => d.StartsWith("需要：", StringComparison.Ordinal));
        }
        finally
        {
            Localizer.SetLanguage(previous);
        }
    }

    [Fact]
    public void MissingChineseFallsBackToTheEnglishTextSoNoLineDisappears()
    {
        var withNote = new MapPoi
        {
            Kind = PoiKind.ExtractPmc,
            Name = "Plain",
            Position = new GamePosition(0, 0, 0),
            Base = new MapPoint(0, 0),
            Details = ["Needs: something"],
        };

        // No Chinese supplied: the display must be exactly the English list.
        Assert.Equal(withNote.Details, withNote.DetailLines.Select(l => l.Text));
        Assert.Single(withNote.DetailLines);
    }

    [Fact]
    public void InEnglishEachDetailDrawsAsASingleEnglishLine()
    {
        var poi = new MapPoi
        {
            Kind = PoiKind.ExtractPmc,
            Name = "Dorms V-Ex",
            Position = new GamePosition(0, 0, 0),
            Base = new MapPoint(0, 0),
            Details = ["Needs: 5000 Roubles"],
            DetailsZh = ["需要：5000 卢布"],
        };

        // Pin English explicitly: the shipped default is Chinese now, but this test is about the
        // English rendering. Under English the Chinese line is not shown at all, and there is no
        // English echo to duplicate the one line.
        using var english = new LanguageScope(AppLanguage.English);

        Assert.Single(poi.DetailLines);
        Assert.Equal("Needs: 5000 Roubles", poi.DetailLines[0].Text);
    }

    /// <summary>
    /// The product decision is one line per detail: under Chinese the Chinese text is shown in
    /// place of the English, not above it.
    /// </summary>
    [Fact]
    public void UnderChineseEachDetailDrawsAsASingleChineseLine()
    {
        var poi = new MapPoi
        {
            Kind = PoiKind.ExtractPmc,
            Name = "Dorms V-Ex",
            Position = new GamePosition(0, 0, 0),
            Base = new MapPoint(0, 0),
            Details = ["Needs: 5000 Roubles", "Single use"],
            DetailsZh = ["需要：5000 卢布", "单次使用"],
        };

        var previous = Localizer.Language;
        Localizer.SetLanguage(AppLanguage.ChineseSimplified);

        try
        {
            var lines = poi.DetailLines;

            // One line per detail, in Chinese, with no English original underneath.
            Assert.Equal(2, lines.Count);
            Assert.Equal(new[] { "需要：5000 卢布", "单次使用" }, lines.Select(l => l.Text));
            Assert.DoesNotContain(lines, l => l.Text.Contains("Roubles", StringComparison.Ordinal));
        }
        finally
        {
            Localizer.SetLanguage(previous);
        }
    }

    [Fact]
    public void UnderChineseALineWithoutChineseFallsBackToEnglishIndividually()
    {
        var poi = new MapPoi
        {
            Kind = PoiKind.ExtractPmc,
            Name = "Mixed",
            Position = new GamePosition(0, 0, 0),
            Base = new MapPoint(0, 0),
            Details = ["Needs: 5000 Roubles", "Single use"],
            DetailsZh = ["需要：5000 卢布"],
        };

        var previous = Localizer.Language;
        Localizer.SetLanguage(AppLanguage.ChineseSimplified);

        try
        {
            var lines = poi.DetailLines;

            Assert.Equal(2, lines.Count);
            Assert.Equal("需要：5000 卢布", lines[0].Text);
            Assert.Equal("Single use", lines[1].Text);
        }
        finally
        {
            Localizer.SetLanguage(previous);
        }
    }

    // ---- Floor labels ----------------------------------------------------------------------

    [Theory]
    [InlineData("2nd Floor", "二层")]
    [InlineData("3rd Floor", "三层")]
    [InlineData("4th Floor", "四层")]
    [InlineData("5th Floor", "五层")]
    [InlineData("Underground", "地下")]
    [InlineData("Second Level", "二层")]
    [InlineData("Garage", "车库")]
    [InlineData("Technical", "技术层")]
    [InlineData("Tunnels", "隧道")]
    [InlineData("Bridge", "桥面")]
    [InlineData("Bridge Roof", "桥顶")]
    [InlineData("Bunkers", "地堡")]
    [InlineData("Control Room", "控制室")]
    [InlineData("Engine Room", "引擎室")]
    [InlineData("Engine Room (upper)", "引擎室（上层）")]
    [InlineData("Fuel Pumps", "燃油泵")]
    [InlineData("Fuel Pumps (lower)", "燃油泵（下层）")]
    [InlineData("Gym/Canteen", "健身房/食堂")]
    [InlineData("Helipad", "直升机停机坪")]
    [InlineData("Infirmary", "医务室")]
    [InlineData("Officers' Deck", "军官甲板")]
    [InlineData("Stairs (blocked)", "楼梯（堵塞）")]
    [InlineData("Storage/Security", "仓储/安保")]
    [InlineData("Accommodation (lower)", "居住区（下层）")]
    [InlineData("Accommodation (mid)", "居住区（中层）")]
    [InlineData("Accommodation (upper)", "居住区（上层）")]
    public void FloorLabelsAreTranslated(string english, string chinese)
    {
        Assert.Equal(chinese, Localizer.FloorLabel(AppLanguage.ChineseSimplified, english));

        // In English the label is the exact English name the layer key is built from.
        Assert.Equal(english, Localizer.FloorLabel(AppLanguage.English, english));
    }

    [Fact]
    public void AnUnknownFloorNameFallsBackToItsRawName()
    {
        Assert.Equal("Not A Floor", Localizer.FloorLabel(AppLanguage.ChineseSimplified, "Not A Floor"));
        Assert.Equal("Not A Floor", Localizer.FloorLabel("Not A Floor"));
    }

    [Fact]
    public void EveryFloorNameInTheBundledDataMapsToAKey()
    {
        var names = Catalog.Maps
            .SelectMany(m => m.Floors)
            .Select(f => f.Name)
            .Distinct(StringComparer.Ordinal)
            .Order()
            .ToArray();

        Assert.Equal(26, names.Length);

        foreach (var name in names)
            Assert.False(Localizer.FloorKey(name) is null, $"no Floor.* key for '{name}'");
    }
}
