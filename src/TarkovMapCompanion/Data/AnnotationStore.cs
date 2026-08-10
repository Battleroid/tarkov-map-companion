using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.Data;

/// <summary>
/// The notes you have written on your maps, and the ones you have imported.
/// </summary>
/// <remarks>
/// <para>
/// Saved to <c>annotations.json</c> beside the settings, on every change. There is no undo and no
/// autosave-on-exit: a raid that ends with the app being killed should not lose the labels somebody
/// spent an evening placing.
/// </para>
/// <para>
/// The file is the sharing format too. Somebody who has labeled every building on Streets can hand
/// the file over, and importing merges rather than replaces, so it never costs you your own notes.
/// </para>
/// </remarks>
public sealed class AnnotationStore
{
    public const string FileName = "annotations.json";

    /// <summary>
    /// A ceiling on how many notes one map will hold.
    /// </summary>
    /// <remarks>
    /// Generous enough that no real map is near it, and low enough that importing a malformed
    /// hundred-megabyte file does not silently become a map nobody can read or a frame nobody can
    /// send.
    /// </remarks>
    public const int MaxPerMap = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly List<MapAnnotation> _annotations = [];
    private readonly string _path;

    public AnnotationStore(string? directory = null)
    {
        _path = Path.Combine(directory ?? AppPaths.ConfigDirectory, FileName);
    }

    /// <summary>Raised whenever the set changes, for whoever is drawing them.</summary>
    public event EventHandler? Changed;

    /// <summary>Where the notes live. Named to avoid shadowing System.IO.Path in here.</summary>
    public string FilePath => _path;

    /// <summary>Everything, across every map.</summary>
    public IReadOnlyList<MapAnnotation> All
    {
        get { lock (_gate) return _annotations.ToArray(); }
    }

    /// <summary>The notes for one map, in the order they were added.</summary>
    public IReadOnlyList<MapAnnotation> ForMap(string normalizedName)
    {
        lock (_gate)
        {
            return _annotations
                .Where(a => string.Equals(a.Map, normalizedName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            using var stream = File.OpenRead(_path);
            var file = JsonSerializer.Deserialize<AnnotationFile>(stream, JsonOptions);

            lock (_gate)
            {
                _annotations.Clear();
                _annotations.AddRange(Sanitize(file?.Annotations ?? []));
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt file should cost the notes, not the app. Left on disk rather than
            // overwritten, so it can be looked at.
            Diagnostics.Log.Warn($"could not read {_path}: {ex.Message}");
        }
    }

    /// <summary>Adds one note and saves. Returns null when the text or the map was unusable.</summary>
    public MapAnnotation? Add(string map, double x, double z, string? text, string? author = null)
    {
        if (string.IsNullOrWhiteSpace(map) || MapAnnotation.Clean(text) is not { } clean)
            return null;

        var annotation = new MapAnnotation
        {
            Map = map,
            X = Math.Round(x, 2),
            Z = Math.Round(z, 2),
            Text = clean,
            Author = author,
        };

        lock (_gate)
        {
            if (_annotations.Count(a => string.Equals(a.Map, map, StringComparison.OrdinalIgnoreCase)) >= MaxPerMap)
                return null;

            _annotations.Add(annotation);
        }

        Save();
        return annotation;
    }

    /// <summary>Changes the text of an existing note. False when it is gone or the text is empty.</summary>
    public bool Retext(string id, string? text)
    {
        if (MapAnnotation.Clean(text) is not { } clean)
            return false;

        lock (_gate)
        {
            var found = _annotations.FirstOrDefault(a => a.Id == id);
            if (found is null)
                return false;

            found.Text = clean;
        }

        Save();
        return true;
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            if (_annotations.RemoveAll(a => a.Id == id) == 0)
                return false;
        }

        Save();
        return true;
    }

    /// <summary>Drops every note on one map.</summary>
    public int RemoveAllOn(string map)
    {
        int removed;

        lock (_gate)
            removed = _annotations.RemoveAll(a => string.Equals(a.Map, map, StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
            Save();

        return removed;
    }

    // ---- Files --------------------------------------------------------------

    /// <summary>
    /// Merges a file of notes into what is already here.
    /// </summary>
    /// <remarks>
    /// Merge rather than replace, because the obvious use is somebody handing you their labels for
    /// one map and you keeping your own for the others. Duplicates are skipped on text and
    /// position rather than on id, so importing the same file twice is harmless even though every
    /// copy has been given fresh ids somewhere along the way.
    /// </remarks>
    public int Import(string path)
    {
        var incoming = Read(path);
        var added = 0;

        lock (_gate)
        {
            foreach (var annotation in Sanitize(incoming))
            {
                var onMap = _annotations.Count(a => string.Equals(a.Map, annotation.Map, StringComparison.OrdinalIgnoreCase));
                if (onMap >= MaxPerMap)
                    continue;

                var duplicate = _annotations.Any(a =>
                    string.Equals(a.Map, annotation.Map, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a.Text, annotation.Text, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(a.X - annotation.X) < 0.5
                    && Math.Abs(a.Z - annotation.Z) < 0.5);

                if (duplicate)
                    continue;

                // A fresh id on the way in, so two people who both imported the same starting file
                // and then edited it do not collide when they share.
                annotation.Id = Guid.NewGuid().ToString("N");

                _annotations.Add(annotation);
                added++;
            }
        }

        if (added > 0)
            Save();

        return added;
    }

    /// <summary>Writes every note, or just one map's, to a file somebody else can import.</summary>
    public int Export(string path, string? map = null)
    {
        var chosen = map is null ? All : ForMap(map);

        var file = new AnnotationFile { Annotations = chosen.ToList() };

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));
        return chosen.Count;
    }

    /// <summary>
    /// Reads either the app's own format or a plain <c>map,x,z,text</c> file.
    /// </summary>
    /// <remarks>
    /// CSV is supported because the obvious source of a few hundred building names is a spreadsheet
    /// or a wiki table, and telling somebody to hand-write JSON for that is telling them not to
    /// bother. Chosen by content rather than by extension: a file that starts with a brace is JSON
    /// whatever it has been named.
    /// </remarks>
    internal static IReadOnlyList<MapAnnotation> Read(string path)
    {
        var text = File.ReadAllText(path);

        if (text.TrimStart().StartsWith('{'))
        {
            var file = JsonSerializer.Deserialize<AnnotationFile>(text, JsonOptions);
            return file?.Annotations ?? [];
        }

        if (text.TrimStart().StartsWith('['))
            return JsonSerializer.Deserialize<List<MapAnnotation>>(text, JsonOptions) ?? [];

        return ParseCsv(text);
    }

    /// <summary>Parses <c>map,x,z,text</c>, skipping a header row and anything malformed.</summary>
    internal static IReadOnlyList<MapAnnotation> ParseCsv(string text)
    {
        var found = new List<MapAnnotation>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // Four fields, and the text is whatever is left, so a label may contain commas.
            var parts = line.Split(',', 4);
            if (parts.Length < 4)
                continue;

            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                // The header row lands here, which is exactly where it should.
                continue;
            }

            if (MapAnnotation.Clean(parts[3].Trim().Trim('"')) is not { } clean)
                continue;

            found.Add(new MapAnnotation
            {
                Map = parts[0].Trim().Trim('"').ToLowerInvariant(),
                X = x,
                Z = z,
                Text = clean,
            });
        }

        return found;
    }

    /// <summary>Drops anything unusable and clips the rest, so bad input costs a line not a crash.</summary>
    private static IEnumerable<MapAnnotation> Sanitize(IEnumerable<MapAnnotation> incoming)
    {
        foreach (var annotation in incoming)
        {
            if (string.IsNullOrWhiteSpace(annotation.Map))
                continue;

            if (MapAnnotation.Clean(annotation.Text) is not { } clean)
                continue;

            if (double.IsNaN(annotation.X) || double.IsNaN(annotation.Z)
                || double.IsInfinity(annotation.X) || double.IsInfinity(annotation.Z))
            {
                continue;
            }

            annotation.Text = clean;
            annotation.Map = annotation.Map.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(annotation.Id))
                annotation.Id = Guid.NewGuid().ToString("N");

            yield return annotation;
        }
    }

    /// <summary>
    /// Replaces everything for one map with what a teammate sent.
    /// </summary>
    /// <remarks>
    /// Their notes are held apart from yours by author and never written to disk, so a session
    /// ending takes them with it and nobody accumulates a squad's worth of somebody else's labels.
    /// </remarks>
    public void SetShared(string author, IReadOnlyList<MapAnnotation> theirs)
    {
        lock (_gate)
        {
            _annotations.RemoveAll(a => string.Equals(a.Author, author, StringComparison.OrdinalIgnoreCase));

            foreach (var annotation in Sanitize(theirs).Take(MaxPerMap))
            {
                annotation.Author = author;
                _annotations.Add(annotation);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets everything anybody else shared, for when a session ends.</summary>
    public void ClearShared()
    {
        int removed;

        lock (_gate)
            removed = _annotations.RemoveAll(a => a.Author is not null);

        if (removed > 0)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Your own notes, which are the only ones that are yours to share or save.</summary>
    public IReadOnlyList<MapAnnotation> Own
    {
        get { lock (_gate) return _annotations.Where(a => a.Author is null).ToArray(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Only our own. A teammate's notes are session state, and writing them here would mean
            // they were still on the map next week with no way to work out where they came from.
            var file = new AnnotationFile { Annotations = Own.ToList() };

            var temp = $"{_path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log.Warn($"could not save annotations: {ex.Message}");
        }
        finally
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
