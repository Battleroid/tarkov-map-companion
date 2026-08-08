using TarkovMapCompanion.Data;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Guards the wiki-sourced exit conditions. These are the things that actually cost you a raid --
/// turning up at a cliff without a Red Rebel, or at a paid extract without the Roubles -- so a
/// silent regression in the scrape or the name matching is worth failing a build over.
/// </summary>
public sealed class ExtractNotesTests
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
            cacheDirectory: Path.Combine(Path.GetTempPath(), "tmc-tests-nocache", Guid.NewGuid().ToString("N")));

        store.LoadLocal();
        return store;
    }

    [Fact]
    public void TheBundledNotesLoad()
    {
        var notes = LoadNotes();

        Assert.Equal("wiki snapshot", notes.Origin);
        Assert.True(notes.CountFor("customs") > 20);
        Assert.True(notes.CountFor("woods") > 15);
    }

    [Theory]
    // The conditions people actually get caught by.
    [InlineData("reserve", "Cliff Descent", "Red Rebel")]
    [InlineData("reserve", "Cliff Descent", "No armor vest")]
    [InlineData("reserve", "Sewer Manhole", "No backpack")]
    [InlineData("reserve", "Exit to Woods", "Minefield map")]
    [InlineData("lighthouse", "Mountain Pass", "Red Rebel")]
    [InlineData("woods", "Railway Bridge to Tarkov", "Minefield map")]
    [InlineData("customs", "Dorms V-Ex", "5000 Roubles")]
    [InlineData("customs", "Smugglers' Boat", "Voron")]
    public void KnownConditionsSurvivedTheScrape(string map, string extract, string expected)
    {
        var note = LoadNotes().Find(map, extract);

        Assert.NotNull(note);
        Assert.NotNull(note!.Requirement);
        Assert.Contains(expected, note.Requirement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PaidExtractsKeepTheirPrice()
    {
        // The cost lives in an image filename on the wiki, so it is the first thing a naive
        // markup strip loses.
        var notes = LoadNotes();

        foreach (var (map, name) in new[]
                 {
                     ("customs", "Dorms V-Ex"),
                     ("woods", "Bridge V-Ex"),
                     ("lighthouse", "Road to Military Base V-Ex"),
                     ("streets-of-tarkov", "Primorsky Ave Taxi V-Ex"),
                 })
        {
            var note = notes.Find(map, name);

            Assert.NotNull(note);
            Assert.Matches(@"\d", note!.Requirement ?? "");
            Assert.Contains("Roubles", note.Requirement!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NoRequirementIsRawWikiMarkup()
    {
        var notes = LoadNotes();

        foreach (var map in Catalog.Maps)
        {
            var data = LoadMapData().ForMap(map.NormalizedName);
            if (data?.Extracts is null)
                continue;

            foreach (var extract in data.Extracts)
            {
                var note = notes.Find(map.NormalizedName, LoadMapData().Translate(extract.Name));
                var text = note?.Requirement;

                if (string.IsNullOrEmpty(text))
                    continue;

                Assert.DoesNotContain("[[", text, StringComparison.Ordinal);
                Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
                Assert.DoesNotContain("<font", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("File:", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("'''", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void NameMatchingSurvivesPunctuationDifferences()
    {
        // The wiki and tarkov.dev do not agree on apostrophes and casing.
        Assert.Equal(ExtractNotesStore.Key("Smugglers' Boat"), ExtractNotesStore.Key("Smugglers Boat"));
        Assert.Equal(ExtractNotesStore.Key("ZB-1012"), ExtractNotesStore.Key("zb1012"));
        Assert.Equal(ExtractNotesStore.Key("Road to Customs"), ExtractNotesStore.Key("road-to-customs"));
        Assert.NotEqual(ExtractNotesStore.Key("ZB-013"), ExtractNotesStore.Key("ZB-014"));
    }

    [Fact]
    public void MostExtractsOnTheBigMapsResolveToANote()
    {
        var notes = LoadNotes();
        var store = LoadMapData();

        foreach (var name in new[] { "customs", "woods", "shoreline", "lighthouse", "reserve", "streets-of-tarkov" })
        {
            var data = store.ForMap(name)!;
            var extracts = data.Extracts ?? [];

            var matched = extracts.Count(e => notes.Find(name, store.Translate(e.Name)) is not null);
            var ratio = matched / (double)extracts.Count;

            Assert.True(ratio >= 0.85, $"{name}: only {matched}/{extracts.Count} extracts matched a wiki note");
        }
    }

    [Fact]
    public void ConditionalExtractsAreFlaggedAndOrdinaryOnesAreNot()
    {
        var store = LoadMapData();
        var notes = LoadNotes();
        var map = Catalog.Find("reserve")!;

        var pois = PoiBuilder.Build(map, store.ForMap("reserve")!, store, notes);

        var cliff = pois.Single(p => p.Name == "Cliff Descent");
        Assert.True(cliff.IsConditional);
        Assert.Contains(cliff.Details, d => d.Contains("Red Rebel", StringComparison.OrdinalIgnoreCase));

        var train = pois.Single(p => p.Name == "Armored Train");
        Assert.True(train.IsConditional);
        Assert.True(train.IsSingleUse);

        // A plain always-open Scav gate should carry no warnings at all.
        var fence = pois.Single(p => p.Name == "Checkpoint Fence");
        Assert.False(fence.IsConditional);
        Assert.Empty(fence.Details);
    }

    [Fact]
    public void SwitchGatedExtractsCountAsConditionalEvenWithoutAWikiNote()
    {
        var store = LoadMapData();
        var map = Catalog.Find("customs")!;

        // Built with no notes at all: the switch on ZB-013 must still mark it conditional.
        var pois = PoiBuilder.Build(map, store.ForMap("customs")!, store, notes: null);

        Assert.True(pois.Single(p => p.Name == "ZB-013").IsConditional);
    }

    [Fact]
    public void BuildingWithoutNotesStillWorks()
    {
        var store = LoadMapData();
        var map = Catalog.Find("customs")!;

        var pois = PoiBuilder.Build(map, store.ForMap("customs")!, store, notes: null);

        Assert.NotEmpty(pois);
        Assert.Contains(pois, p => p.Name == "Dorms V-Ex");
    }
}
