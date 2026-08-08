using TarkovMapCompanion.Data;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// The settings file addresses layers and heatmap bands by enum <em>name</em>, so a rename or a
/// typo turns into a silent lookup miss rather than a compile error. These tests make that loud.
/// </summary>
public sealed class SettingsKeyTests
{
    [Fact]
    public void HeatmapCategoryKeys_MatchTheSpawnGroupEnumExactly()
    {
        var keys = new AppSettings().HeatmapCategories.Keys.Order().ToArray();
        var names = Enum.GetNames<SpawnGroup>().Order().ToArray();

        Assert.Equal(names, keys);
    }

    [Fact]
    public void PoiLayerKeys_CoverEveryPoiKind()
    {
        var settings = new AppSettings();

        foreach (var kind in Enum.GetValues<PoiKind>())
        {
            Assert.True(
                settings.PoiLayers.ContainsKey(kind.ToString()),
                $"default settings have no entry for the {kind} layer");
        }
    }

    [Fact]
    public void PoiLayerKeys_HaveNoEntriesThatMatchNothing()
    {
        var settings = new AppSettings();
        var known = Enum.GetNames<PoiKind>().ToHashSet(StringComparer.Ordinal);

        var orphans = settings.PoiLayers.Keys.Where(k => !known.Contains(k)).ToArray();

        Assert.True(orphans.Length == 0, $"stale layer keys: {string.Join(", ", orphans)}");
    }

    [Fact]
    public void DefaultLayers_ShowExitsAndHideTheDenseOnes()
    {
        var settings = new AppSettings();

        // Exits are the point of the app; loose loot and spawns would bury the map.
        Assert.True(settings.PoiLayers["ExtractPmc"]);
        Assert.True(settings.PoiLayers["ExtractScav"]);
        Assert.True(settings.PoiLayers["ExtractShared"]);
        Assert.True(settings.PoiLayers["Transit"]);

        Assert.False(settings.PoiLayers["LootContainer"]);
        Assert.False(settings.PoiLayers["Spawn"]);
    }

    [Fact]
    public void DefaultHeatmapBands_ArePmcAndScav()
    {
        var settings = new AppSettings();

        Assert.True(settings.HeatmapCategories["Pmc"]);
        Assert.True(settings.HeatmapCategories["Scav"]);
        Assert.False(settings.HeatmapCategories["AiPmc"]);
        Assert.False(settings.HeatmapCategories["Boss"]);
    }

    [Fact]
    public void SettingsDrivesTheOverlayBands_NotTheOtherWayAround()
    {
        // Reproduces the mismatch that let a settings file and the overlay disagree: every enum
        // name must resolve, so flipping a setting actually reaches the overlay.
        var settings = new AppSettings();
        var overlay = new HeatmapOverlay();

        settings.HeatmapCategories["Pmc"] = false;
        settings.HeatmapCategories["AiPmc"] = true;

        foreach (var group in Enum.GetValues<SpawnGroup>())
        {
            Assert.True(
                settings.HeatmapCategories.TryGetValue(group.ToString(), out var on),
                $"no settings key resolves for {group}");

            overlay.Groups[group] = on;
        }

        Assert.False(overlay.Groups[SpawnGroup.Pmc]);
        Assert.True(overlay.Groups[SpawnGroup.AiPmc]);
        Assert.True(overlay.Groups[SpawnGroup.Scav]);
    }
}
