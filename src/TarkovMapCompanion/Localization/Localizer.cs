using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.Localization;

/// <summary>
/// Dictionary-lookup localization for the handful of strings the MVP surfaces.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not <c>.resx</c>/satellite assemblies. <c>InvariantGlobalization</c> is on and the
/// app already resolves data names through a plain string dictionary
/// (<see cref="Data.MapDataStore.Translate"/>), so a dictionary that lives inside the assembly is
/// the shape that fits. Two flat JSON documents are embedded: <c>StringsEn.json</c> is the source
/// of truth for the keys and <c>StringsZh.json</c> is the Simplified-Chinese overlay. The names
/// avoid a <c>Strings.en.json</c> / <c>Strings.zh.json</c> form, which MSBuild would read as a
/// culture and ship as a satellite assembly instead of embedding it.
/// </para>
/// <para>
/// The active language is a process-wide static, mirroring how the theme is resolved at startup
/// rather than injected. A language change is applied on restart, so nothing here has to notify
/// already-built windows.
/// </para>
/// </remarks>
public static class Localizer
{
    private const string ResourcePrefix = "TarkovMapCompanion.Localization.Strings";

    private static readonly object Gate = new();

    private static Dictionary<string, string>? _english;
    private static Dictionary<string, string>? _chinese;

    /// <summary>The language the UI is drawn in. Set once at startup from the saved settings.</summary>
    public static AppLanguage Language { get; private set; } = AppLanguage.English;

    /// <summary>Applies the saved language. Takes effect for everything built afterwards.</summary>
    public static void SetLanguage(AppLanguage language) => Language = language;

    /// <summary>
    /// Resolves a key: the active language first, then English, then the key itself. Never throws
    /// on a missing key -- a half-translated UI should read oddly, not crash.
    /// </summary>
    public static string Loc(string key)
    {
        if (string.IsNullOrEmpty(key))
            return key;

        EnsureLoaded();

        if (Language == AppLanguage.ChineseSimplified
            && _chinese!.TryGetValue(key, out var zh) && zh.Length > 0)
        {
            return zh;
        }

        if (_english!.TryGetValue(key, out var en) && en.Length > 0)
            return en;

        // English is the source of truth, but fall back to the overlay too so a key that only ever
        // got a Chinese entry still resolves rather than showing as its dotted name.
        if (_chinese!.TryGetValue(key, out var stray) && stray.Length > 0)
            return stray;

        return key;
    }

    /// <summary>
    /// Resolves a key against the English dictionary regardless of the active language.
    /// </summary>
    /// <remarks>
    /// Used for the English echo shown under a translated line: that echo has to read as English
    /// throughout, so it cannot go through <see cref="Loc(string)"/>, which would wrap it in the
    /// active language's text.
    /// </remarks>
    public static string LocEnglish(string key)
    {
        if (string.IsNullOrEmpty(key))
            return key;

        EnsureLoaded();

        if (_english!.TryGetValue(key, out var en) && en.Length > 0)
            return en;

        if (_chinese!.TryGetValue(key, out var zh) && zh.Length > 0)
            return zh;

        return key;
    }

    /// <summary>English form of <see cref="Loc(string, object[])"/>, invariant-formatted.</summary>
    public static string LocEnglish(string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, LocEnglish(key), args);

    /// <summary>Formats a resolved string with the invariant culture.</summary>
    public static string Loc(string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, Loc(key), args);

    /// <summary>
    /// Chooses between an English and a Chinese value for the active language, falling back to the
    /// English one whenever no Chinese value exists. The single decision point the display layer
    /// uses, so no call site has to branch on the language itself.
    /// </summary>
    public static string Pick(string english, string? chinese) => Pick(Language, english, chinese);

    /// <summary>
    /// Language-parameterised form of <see cref="Pick(string, string?)"/>, so the choice can be
    /// exercised without touching the process-wide language.
    /// </summary>
    public static string Pick(AppLanguage language, string english, string? chinese) =>
        language == AppLanguage.ChineseSimplified && !string.IsNullOrWhiteSpace(chinese)
            ? chinese!
            : english;

    /// <summary>
    /// Display name for a theme option.
    /// </summary>
    /// <remarks>
    /// The <see cref="AppTheme"/> value is what settings.json persists, so only the label shown in
    /// the picker and the status line is localized -- never the stored value.
    /// </remarks>
    public static string ThemeLabel(AppTheme theme) => Loc(theme switch
    {
        AppTheme.Light => "Theme.Light",
        AppTheme.Dark => "Theme.Dark",
        _ => "Theme.System",
    });

    /// <summary>
    /// Display name for a map floor.
    /// </summary>
    /// <remarks>
    /// <c>MapFloor.Name</c> is also the layer lookup key the renderer and the floor toggles use, so
    /// it is never translated in place: only the label shown on the checkbox goes through here,
    /// keyed off the exact English name. An unrecognised name falls back to itself.
    /// </remarks>
    public static string FloorLabel(string englishName) => FloorLabel(Language, englishName);

    /// <summary>
    /// Language-parameterised form of <see cref="FloorLabel(string)"/>, so the table can be
    /// exercised without touching the process-wide language.
    /// </summary>
    internal static string FloorLabel(AppLanguage language, string englishName)
    {
        var key = FloorKey(englishName);
        if (key is null)
            return englishName;

        EnsureLoaded();

        var table = language == AppLanguage.ChineseSimplified ? _chinese! : _english!;
        if (table.TryGetValue(key, out var value) && value.Length > 0)
            return value;

        if (_english!.TryGetValue(key, out var english) && english.Length > 0)
            return english;

        return englishName;
    }

    /// <summary>Maps an English floor name to its <c>Floor.*</c> key, or null when unknown.</summary>
    internal static string? FloorKey(string englishName) => englishName switch
    {
        "2nd Floor" => "Floor.2ndFloor",
        "3rd Floor" => "Floor.3rdFloor",
        "4th Floor" => "Floor.4thFloor",
        "5th Floor" => "Floor.5thFloor",
        "Underground" => "Floor.Underground",
        "Second Level" => "Floor.SecondLevel",
        "Garage" => "Floor.Garage",
        "Technical" => "Floor.Technical",
        "Tunnels" => "Floor.Tunnels",
        "Bridge" => "Floor.Bridge",
        "Bridge Roof" => "Floor.BridgeRoof",
        "Bunkers" => "Floor.Bunkers",
        "Control Room" => "Floor.ControlRoom",
        "Engine Room" => "Floor.EngineRoom",
        "Engine Room (upper)" => "Floor.EngineRoomUpper",
        "Fuel Pumps" => "Floor.FuelPumps",
        "Fuel Pumps (lower)" => "Floor.FuelPumpsLower",
        "Gym/Canteen" => "Floor.GymCanteen",
        "Helipad" => "Floor.Helipad",
        "Infirmary" => "Floor.Infirmary",
        "Officers' Deck" => "Floor.OfficersDeck",
        "Stairs (blocked)" => "Floor.StairsBlocked",
        "Storage/Security" => "Floor.StorageSecurity",
        "Accommodation (lower)" => "Floor.AccommodationLower",
        "Accommodation (mid)" => "Floor.AccommodationMid",
        "Accommodation (upper)" => "Floor.AccommodationUpper",
        _ => null,
    };

    private static void EnsureLoaded()
    {
        if (_english is not null)
            return;

        lock (Gate)
        {
            if (_english is not null)
                return;

            _english = Load("En");
            _chinese = Load("Zh");
        }
    }

    private static Dictionary<string, string> Load(string language)
    {
        var name = ResourcePrefix + language + ".json";

        try
        {
            var assembly = typeof(Localizer).GetTypeInfo().Assembly;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                Console.Error.WriteLine($"localization: embedded resource '{name}' is missing");
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var json = reader.ReadToEnd();

            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.Error.WriteLine($"localization: could not read '{name}': {ex.Message}");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
