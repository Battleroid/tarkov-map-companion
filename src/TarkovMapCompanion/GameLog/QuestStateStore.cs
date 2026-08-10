using System.Text.Json;
using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.GameLog;

/// <summary>
/// Remembers what the logs said about each quest, so a cleared log folder does not lose it.
/// </summary>
/// <remarks>
/// Tarkov keeps one log folder per launch and prunes none of them, so in practice the history is
/// there. In practice is not always: people clean folders, move installs and reinstall, and the
/// failure mode without this is silent -- every quest quietly reverts to "never started" and the
/// tracker empties itself.
/// </remarks>
public sealed class QuestStateStore
{
    /// <summary>
    /// Named for the shape rather than the contents, because the shape changed.
    /// </summary>
    /// <remarks>
    /// The first version was one flat map of task to progress for the whole account, which is
    /// wrong on any account with both a PVE and a PVP character: it merged two people's quests
    /// into one answer. The old file is left where it is and ignored -- what it holds cannot be
    /// split back apart, and the logs rebuild it correctly anyway.
    /// </remarks>
    public const string FileName = "quest-state-by-profile.json";

    /// <summary>The pre-profile file. Read by nothing; named so nobody wonders what it is.</summary>
    public const string LegacyFileName = "quest-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public QuestStateStore(string? directory = null)
    {
        _path = Path.Combine(directory ?? AppPaths.CacheDirectory, FileName);
    }

    public string FilePath => _path;

    /// <summary>Every character's quest state, keyed by profile id.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, QuestProgress>> Load()
    {
        var empty = new Dictionary<string, IReadOnlyDictionary<string, QuestProgress>>(StringComparer.Ordinal);

        try
        {
            if (!File.Exists(_path))
                return empty;

            using var stream = File.OpenRead(_path);

            var stored = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, QuestProgress>>>(
                stream, JsonOptions);

            if (stored is null)
                return empty;

            return stored.ToDictionary(
                p => p.Key,
                p => (IReadOnlyDictionary<string, QuestProgress>)p.Value,
                StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log.Warn($"could not read {_path}: {ex.Message}");
            return empty;
        }
    }

    public void Save(IReadOnlyDictionary<string, IReadOnlyDictionary<string, QuestProgress>> state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var temp = $"{_path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Rebuilt from the logs next launch, which is the normal path anyway.
        }
    }
}
