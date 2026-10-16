using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Restoring the selected extract when the saved id is one the pmc/scav fold no longer puts on any
/// POI, so the name is the only thing left to match on.
/// </summary>
public sealed class SavedExtractRestoreTests
{
    private static AppSettings Settings() => new()
    {
        AllowNetwork = false,
        CurrentMap = "customs",
        CullMode = CullMode.Off,
        ScreenshotFolder = Path.Combine(Path.GetTempPath(), "tmc-saved-extract-noshots"),
    };

    /// <summary>
    /// The name that bridges a saved id to its POI is the English one, so the restore survives a
    /// Chinese UI.
    /// </summary>
    /// <remarks>
    /// Upstream ships one physical exit as two records -- a pmc half and a scav half sharing a name
    /// -- and the builder folds them into one POI that keeps a single id. A selection saved against
    /// the other half therefore matches no POI by id and has to fall back to the name. The name it
    /// compared used to be the display translation, which stopped matching the POI's always-English
    /// name the moment the app defaulted to Simplified Chinese, silently dropping the selection.
    /// </remarks>
    [Fact]
    public void ASavedIdThatNoPoiCarriesIsRestoredByItsEnglishNameUnderChinese()
    {
        using var language = new LanguageScope(AppLanguage.ChineseSimplified);

        var settings = Settings();
        using var session = new MapSession(settings, MapCatalog.LoadEmbedded());
        session.MapData.LoadLocal();
        session.RebuildPoisForTesting();

        var data = session.MapData.ForMap(session.CurrentMap.NormalizedName);
        Assert.NotNull(data);

        // A raw record with a real Chinese translation that is folded under a POI carrying a
        // different id: exactly the case the exact-id lookup cannot serve.
        var candidate = data!.Extracts!
            .Where(e => e.Position is not null && !string.IsNullOrEmpty(e.Id))
            .FirstOrDefault(e =>
            {
                var english = session.MapData.TranslateEn(e.Name);
                var chinese = session.MapData.TranslateZh(e.Name);
                if (chinese.Length == 0 || string.Equals(chinese, english, StringComparison.Ordinal))
                    return false;

                var poi = session.Pois.Extracts.FirstOrDefault(p =>
                    string.Equals(p.Name, english, StringComparison.Ordinal));

                return poi is not null && !string.Equals(poi.Id, e.Id, StringComparison.Ordinal);
            });

        Assert.NotNull(candidate);

        settings.SelectedExtractId = candidate!.Id;
        session.RebuildPoisForTesting();

        var restored = session.SelectedExtract;

        Assert.NotNull(restored);
        Assert.Equal(session.MapData.TranslateEn(candidate.Name), restored!.Name);
    }
}
