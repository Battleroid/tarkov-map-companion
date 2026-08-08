using System.Text.Json;

namespace TarkovMapCompanion.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON under %APPDATA%.
/// </summary>
/// <remarks>
/// Writes go through a temp file + replace so a crash mid-save cannot leave a truncated settings
/// file behind, and a corrupt file on load is moved aside rather than deleted -- losing someone's
/// preferences silently is worse than starting from defaults with the broken file still on disk.
/// </remarks>
public sealed class SettingsStore
{
    /// <summary>
    /// Source-generated metadata plus the leniency a hand-editable file needs.
    /// <c>JsonSourceGenerationOptions</c> on .NET 8 cannot express comment/trailing-comma handling,
    /// so those are layered on here while still resolving type info from the generated context.
    /// </summary>
    private static readonly JsonSerializerOptions ReadOptions = new(SettingsJsonContext.Default.Options)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly object _writeLock = new();

    public SettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    public string Path => _path;

    public static string DefaultPath() => System.IO.Path.Combine(AppPaths.ConfigDirectory, "settings.json");

    public AppSettings Load()
    {
        AppSettings settings;

        try
        {
            if (!File.Exists(_path))
            {
                settings = new AppSettings();
            }
            else
            {
                var json = File.ReadAllText(_path);
                settings = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            QuarantineCorruptFile(ex);
            settings = new AppSettings();
        }

        settings.Normalize();
        return settings;
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();

        var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);

        lock (_writeLock)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);

            if (File.Exists(_path))
            {
                // Replace is atomic on NTFS and keeps the original if the swap fails.
                File.Replace(temp, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _path);
            }
        }
    }

    private void QuarantineCorruptFile(Exception cause)
    {
        try
        {
            if (!File.Exists(_path))
                return;

            var quarantine = _path + ".corrupt";
            File.Move(_path, quarantine, overwrite: true);
            Console.Error.WriteLine($"settings: could not read {_path} ({cause.Message}); moved to {quarantine}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"settings: could not quarantine {_path}: {ex.Message}");
        }
    }
}

/// <summary>Well-known per-user locations. Centralised so tests can reason about them.</summary>
public static class AppPaths
{
    public const string AppFolderName = "TarkovMapCompanion";

    /// <summary>Roaming: preferences and hand-editable overlays.</summary>
    public static string ConfigDirectory => EnsureDirectory(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName));

    /// <summary>Local: regenerable caches (map data, tiles). Should never hold anything precious.</summary>
    public static string CacheDirectory => EnsureDirectory(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName, "cache"));

    public static string TileCacheDirectory => EnsureDirectory(System.IO.Path.Combine(CacheDirectory, "tiles"));

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
