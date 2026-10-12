using TarkovMapCompanion.Data;
using TarkovMapCompanion.Data.Models;
using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Localization;

/// <summary>
/// The localized display name of a map, keyed by normalized name.
/// </summary>
/// <remarks>
/// <para>
/// The names live in the map-data translations, keyed <c>&lt;mapObjectId&gt; Name</c> (for Customs
/// that is <c>56f40101d2720b2a4d8b45d6 Name</c>). The catalog itself carries no display name, so
/// this table is the bridge from a <see cref="GameMap"/> to those two strings.
/// </para>
/// <para>
/// Loaded lazily from the embedded snapshot the first time anything asks, then replaced wholesale
/// whenever <see cref="MapDataStore"/> loads fresher data. Reading the embedded snapshot directly
/// means a map's name is right even when it is constructed before the data store has loaded.
/// </para>
/// </remarks>
public static class MapNames
{
    private static readonly object Gate = new();

    private static Dictionary<string, string>? _english;
    private static Dictionary<string, string>? _chinese;

    /// <summary>English display name from the data, or null when the data names no such map.</summary>
    public static string? EnglishFor(string normalizedName)
    {
        EnsureLoaded();
        return _english!.TryGetValue(normalizedName, out var name) ? name : null;
    }

    /// <summary>Simplified-Chinese display name from the data, or null when there is none.</summary>
    public static string? ChineseFor(string normalizedName)
    {
        EnsureLoaded();
        return _chinese!.TryGetValue(normalizedName, out var name) ? name : null;
    }

    /// <summary>
    /// The name to show for a map in the active language, falling back to the derived English name
    /// when the data has nothing to say.
    /// </summary>
    public static string Display(string normalizedName)
    {
        var english = EnglishFor(normalizedName) ?? GameMap.ToDisplayName(normalizedName);
        return Localizer.Pick(english, ChineseFor(normalizedName));
    }

    /// <summary>
    /// Rebuilds the table from a freshly loaded document. Called by <see cref="MapDataStore"/> so a
    /// network refresh is reflected in map names as well as POI text.
    /// </summary>
    public static void Apply(MapDataDocument document)
    {
        var (english, chinese) = Build(document);

        lock (Gate)
        {
            _english = english;
            _chinese = chinese;
        }
    }

    private static void EnsureLoaded()
    {
        if (_english is not null)
            return;

        lock (Gate)
        {
            if (_english is not null)
                return;

            var snapshot = MapDataStore.EmbeddedSnapshot();
            var (english, chinese) = snapshot is null
                ? (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                   new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                : Build(snapshot);

            _english = english;
            _chinese = chinese;
        }
    }

    private static (Dictionary<string, string> English, Dictionary<string, string> Chinese) Build(
        MapDataDocument document)
    {
        var english = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var chinese = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var map in document.Maps.Values)
        {
            if (string.IsNullOrEmpty(map.NormalizedName))
                continue;

            var key = map.Id is { Length: > 0 } id ? id + " Name" : null;
            if (key is null)
                continue;

            if (document.Translations.TryGetValue(key, out var en) && !string.IsNullOrWhiteSpace(en))
                english[map.NormalizedName] = en;

            if (document.TranslationsZh.TryGetValue(key, out var zh) && !string.IsNullOrWhiteSpace(zh))
                chinese[map.NormalizedName] = zh;
        }

        return (english, chinese);
    }
}
