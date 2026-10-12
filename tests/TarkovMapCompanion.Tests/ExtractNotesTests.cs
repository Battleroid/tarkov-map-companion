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

        // Loose floors: the wiki reworks its extraction tables between snapshots (Customs alone
        // went from 26 rows to 16 in one such rework, dropping retired exits and adding renamed
        // ones), so this only guards that the file loaded at all, not an exact row count.
        Assert.True(notes.CountFor("customs") > 10);
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

    [Theory]
    // Tails that sat beyond the old 190-character cap, so a regression back to a short cap would
    // silently drop the actionable half of each requirement again. Every one of these was verified
    // to be absent from the 190-character rendering of the same live text.
    [InlineData("interchange", "Saferoom Exfil", "the button next to it")]
    [InlineData("terminal", "Zubr Boat", "TerraGroup evidence")]
    [InlineData("customs", "Railroad Passage (Flare)", "Signal pistol")]
    [InlineData("ground-zero", "Mira Ave (Flare)", "Signal pistol")]
    [InlineData("woods", "Power Line Passage (Flare)", "Only one person amongst a lobby-made group")]
    public void LongRequirementsKeepTheirActionableTail(string map, string extract, string expected)
    {
        var note = LoadNotes().Find(map, extract);

        Assert.NotNull(note);
        Assert.NotNull(note!.Requirement);
        Assert.Contains(expected, note.Requirement, StringComparison.Ordinal);
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

            // The floor is deliberately loose. Upstream renames and adds extracts between data
            // refreshes (Customs' ZB-013 became ZB-1013; several maps gained renamed exits) while
            // the hand-maintained wiki snapshot lags, so the raw ratio moves by tens of points
            // without the translation merge being broken. A merge failure -- unresolved keys --
            // would take this to near zero, which is what the test is really guarding.
            Assert.True(ratio >= 0.6, $"{name}: only {matched}/{extracts.Count} extracts matched a wiki note");
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

    /// <summary>
    /// Customs' switch link is mis-assigned upstream -- one Reserve switch id sits on all 28 extracts
    /// -- so it must not mark the whole map conditional. Built with no notes at all, ZB-1013 has no
    /// condition of its own here, so it must read as an ordinary extract; its real gating is the wiki
    /// requirement, which the conditional tests that load notes exercise.
    /// </summary>
    [Fact]
    public void AMisassignedSwitchDoesNotMakeEveryExtractConditional()
    {
        var store = LoadMapData();
        var map = Catalog.Find("customs")!;

        var pois = PoiBuilder.Build(map, store.ForMap("customs")!, store, notes: null);

        Assert.False(pois.Single(p => p.Name == "ZB-1013").IsConditional);
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
