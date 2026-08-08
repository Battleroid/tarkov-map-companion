using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.Data;

/// <summary>
/// Extraction conditions gathered from the Escape from Tarkov wiki.
/// </summary>
/// <remarks>
/// <para>
/// The tarkov.dev data knows an extract's faction, position and any switch that gates it, but not
/// the conditions players actually get caught out by: the Roubles a paid extract charges, the
/// Red Rebel and paracord a cliff descent needs, whether an extract only opens for part of the
/// raid, or whether it is single use. Those live in prose on the wiki.
/// </para>
/// <para>
/// Community data, and marked as such in the UI: it is maintained by hand and can lag a patch.
/// Regenerate with <c>--fetch-wiki</c>. Wiki text is CC BY-SA; only the short structured
/// requirement fields are carried over, and the About screen credits the wiki.
/// </para>
/// </remarks>
public sealed class ExtractNotesDocument
{
    [JsonPropertyName("fetchedAt")] public DateTimeOffset? FetchedAt { get; set; }

    /// <summary>Keyed by map normalized name.</summary>
    [JsonPropertyName("maps")] public Dictionary<string, List<ExtractNote>> Maps { get; set; } = [];
}

public sealed class ExtractNote
{
    /// <summary>Extract name as the wiki spells it; matched loosely against the tarkov.dev name.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>
    /// False when the extract is not always open: it needs a trigger, a payment, a timed window,
    /// or is otherwise conditional. This is the flag that makes an extract worth annotating.
    /// </summary>
    [JsonPropertyName("alwaysAvailable")] public bool? AlwaysAvailable { get; set; }

    /// <summary>Set when availability differs by faction, e.g. open to Scavs but not PMCs.</summary>
    [JsonPropertyName("availability")] public string? Availability { get; set; }

    /// <summary>Usable once per raid, so a squad cannot all follow you through.</summary>
    [JsonPropertyName("singleUse")] public bool? SingleUse { get; set; }

    /// <summary>What you need in order to use it, condensed to a line or two.</summary>
    [JsonPropertyName("requirement")] public string? Requirement { get; set; }
}

/// <summary>
/// Loads extract notes, letting a hand-edited user file override the bundled data.
/// </summary>
public sealed class ExtractNotesStore
{
    public const string UserFileName = "extract-notes.json";
    private const string SnapshotResourceName = "TarkovMapCompanion.Data.Snapshots.extract-notes.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    private readonly Dictionary<string, Dictionary<string, ExtractNote>> _byMap = new(StringComparer.OrdinalIgnoreCase);

    public string Origin { get; private set; } = "none";

    /// <summary>Path a user can create to correct or extend the bundled notes.</summary>
    public static string UserFilePath => Path.Combine(AppPaths.ConfigDirectory, UserFileName);

    public void Load()
    {
        var bundled = ReadEmbedded();
        var user = ReadUserFile();

        Merge(bundled);

        if (user is not null)
        {
            // The user file wins per extract, so correcting one line does not mean maintaining
            // the whole file.
            Merge(user);
            Origin = bundled is null ? "your notes file" : "wiki snapshot plus your notes";
        }
        else
        {
            Origin = bundled is null ? "none" : "wiki snapshot";
        }
    }

    /// <summary>Note for an extract, matched leniently on name.</summary>
    public ExtractNote? Find(string mapNormalizedName, string extractName)
    {
        if (!_byMap.TryGetValue(mapNormalizedName, out var notes))
            return null;

        return notes.TryGetValue(Key(extractName), out var note) ? note : null;
    }

    public int CountFor(string mapNormalizedName) =>
        _byMap.TryGetValue(mapNormalizedName, out var notes) ? notes.Count : 0;

    private void Merge(ExtractNotesDocument? document)
    {
        if (document is null)
            return;

        foreach (var (map, notes) in document.Maps)
        {
            if (!_byMap.TryGetValue(map, out var bucket))
                _byMap[map] = bucket = new Dictionary<string, ExtractNote>(StringComparer.Ordinal);

            foreach (var note in notes)
            {
                if (!string.IsNullOrWhiteSpace(note.Name))
                    bucket[Key(note.Name)] = note;
            }
        }
    }

    /// <summary>
    /// Match key. The wiki and tarkov.dev disagree on punctuation and casing often enough
    /// ("Smugglers' Path" vs "Smugglers Path") that exact matching loses real entries.
    /// </summary>
    internal static string Key(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        var length = 0;

        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
                buffer[length++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..length]);
    }

    private static ExtractNotesDocument? ReadEmbedded()
    {
        try
        {
            using var stream = System.Reflection.Assembly
                .GetExecutingAssembly()
                .GetManifestResourceStream(SnapshotResourceName);

            return stream is null ? null : JsonSerializer.Deserialize<ExtractNotesDocument>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"extract notes: bundled snapshot unreadable: {ex.Message}");
            return null;
        }
    }

    private static ExtractNotesDocument? ReadUserFile()
    {
        try
        {
            var path = UserFilePath;
            if (!File.Exists(path))
                return null;

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<ExtractNotesDocument>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A broken user file must not cost the bundled notes.
            Console.Error.WriteLine($"extract notes: ignoring unreadable user file: {ex.Message}");
            return null;
        }
    }

    public static void Save(ExtractNotesDocument document, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions));
    }
}
