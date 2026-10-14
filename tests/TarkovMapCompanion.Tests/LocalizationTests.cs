using TarkovMapCompanion.Data;
using TarkovMapCompanion.Localization;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// The localization MVP: the dictionary lookup, the map-name table, and that the embedded
/// snapshots actually carry the Chinese data.
/// </summary>
/// <remarks>
/// Nothing here mutates <see cref="Localizer.Language"/>: the process-wide language is set once at
/// startup and tests for different classes run in parallel, so flipping it would make unrelated
/// assertions depend on ordering. The language-sensitive choice is exercised through the
/// language-parameterised overload instead.
/// </remarks>
public sealed class LocalizationTests
{
    [Fact]
    public void ChineseIsTheDefaultWhileEnglishIsTheSourceOfTruth()
    {
        // A new install with no settings.json draws in Simplified Chinese.
        Assert.Equal(AppLanguage.ChineseSimplified, Localizer.Language);

        // English stays the source of truth even when it is not the active language...
        Assert.Equal("ahead", Localizer.LocEnglish("extract.ahead"));
        Assert.Equal("Language", Localizer.LocEnglish("prefs.language"));

        // ...and the active language resolves through the Chinese overlay.
        Assert.Equal("语言", Localizer.Loc("prefs.language"));
    }

    [Fact]
    public void AMissingKeyFallsBackToTheKeyItself()
    {
        Assert.Equal("no.such.key.exists", Localizer.Loc("no.such.key.exists"));
    }

    [Theory]
    [InlineData(AppLanguage.English, "Customs", "海关", "Customs")]
    [InlineData(AppLanguage.ChineseSimplified, "Customs", "海关", "海关")]
    [InlineData(AppLanguage.ChineseSimplified, "Foo", null, "Foo")]
    [InlineData(AppLanguage.ChineseSimplified, "Foo", "", "Foo")]
    public void PickChoosesByLanguageAndFallsBackToEnglish(
        AppLanguage language, string english, string? chinese, string expected) =>
        Assert.Equal(expected, Localizer.Pick(language, english, chinese));

    [Fact]
    public void TheEmbeddedMapDataCarriesChineseTranslations()
    {
        var snapshot = MapDataStore.EmbeddedSnapshot();

        Assert.NotNull(snapshot);
        Assert.NotEmpty(snapshot!.TranslationsZh);

        // The map's own name is in there under "<mapObjectId> Name" (Customs -> 海关).
        Assert.Contains("海关", snapshot.TranslationsZh.Values);
    }

    [Fact]
    public void MapNamesResolveInBothLanguages()
    {
        Assert.Equal("Customs", MapNames.EnglishFor("customs"));
        Assert.Equal("海关", MapNames.ChineseFor("customs"));
    }

    [Fact]
    public void TheEmbeddedTaskSnapshotCarriesChineseNames()
    {
        var snapshot = TaskStore.EmbeddedSnapshot();

        Assert.NotNull(snapshot);
        Assert.Contains(snapshot!.Tasks, t => !string.IsNullOrEmpty(t.NameZh));
        Assert.Contains(
            snapshot.Tasks.SelectMany(t => t.Objectives),
            o => !string.IsNullOrEmpty(o.DescriptionZh));
    }

    [Fact]
    public void PoisCarryChineseAlongsideTheEnglishNameOcrNeeds()
    {
        var store = new MapDataStore(
            new AppSettings { AllowNetwork = false },
            cacheDirectory: Path.Combine(Path.GetTempPath(), "tmc-loc-tests", Guid.NewGuid().ToString("N")));
        store.LoadLocal();

        var map = MapCatalog.LoadEmbedded().Find("customs")!;
        var pois = PoiBuilder.Build(map, store.ForMap("customs")!, store);

        var dorms = pois.FirstOrDefault(p => p.Name == "Dorms V-Ex");
        Assert.NotNull(dorms);

        // The English name never goes away -- OCR still matches against it -- while the Chinese
        // name rides alongside. The display helper follows the active language, which defaults to
        // Chinese now.
        Assert.False(string.IsNullOrEmpty(dorms!.NameZh));
        Assert.NotEqual(dorms.Name, dorms.NameZh);
        Assert.Equal(dorms.NameZh, dorms.DisplayName);
    }
}
