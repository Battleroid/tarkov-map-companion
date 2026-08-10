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
    public const string FileName = "quest-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public QuestStateStore(string? directory = null)
    {
        _path = Path.Combine(directory ?? AppPaths.CacheDirectory, FileName);
    }

    public string FilePath => _path;

    public IReadOnlyDictionary<string, QuestProgress> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new Dictionary<string, QuestProgress>(StringComparer.Ordinal);

            using var stream = File.OpenRead(_path);

            var stored = JsonSerializer.Deserialize<Dictionary<string, QuestProgress>>(stream, JsonOptions);
            return stored ?? new Dictionary<string, QuestProgress>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log.Warn($"could not read {_path}: {ex.Message}");
            return new Dictionary<string, QuestProgress>(StringComparer.Ordinal);
        }
    }

    public void Save(IReadOnlyDictionary<string, QuestProgress> state)
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
