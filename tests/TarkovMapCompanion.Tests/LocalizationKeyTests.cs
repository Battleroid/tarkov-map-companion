using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using TarkovMapCompanion.Localization;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Guards the localization dictionaries against extraction drift.
/// </summary>
/// <remarks>
/// Every key a view or a code-behind references must exist in <c>StringsEn.json</c>, and the Chinese
/// overlay may only contain keys that English also defines. Both failures are silent at runtime --
/// a missing key just renders as its own dotted name -- so they are worth failing a build over.
/// </remarks>
public sealed class LocalizationKeyTests
{
    private static readonly Regex XamlKey = new(@"\{loc:Tr\s+([^\s\}]+)", RegexOptions.Compiled);
    private static readonly Regex CodeKey = new(@"Loc\(\s*""([^""]+)""", RegexOptions.Compiled);

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string SourceDir => Path.Combine(RepoRoot(), "src", "TarkovMapCompanion");

    private static Dictionary<string, string> ReadDictionary(string file) =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(SourceDir, "Localization", file)))!;

    private static HashSet<string> ReferencedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(SourceDir, "*.axaml", SearchOption.AllDirectories))
        {
            foreach (Match m in XamlKey.Matches(File.ReadAllText(file)))
                keys.Add(m.Groups[1].Value);
        }

        foreach (var file in Directory.EnumerateFiles(SourceDir, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match m in CodeKey.Matches(File.ReadAllText(file)))
                keys.Add(m.Groups[1].Value);
        }

        return keys;
    }

    [Fact]
    public void EveryReferencedKeyExistsInTheEnglishSourceOfTruth()
    {
        var english = ReadDictionary("StringsEn.json");
        var missing = ReferencedKeys().Where(k => !english.ContainsKey(k)).Order().ToArray();

        Assert.True(missing.Length == 0, $"keys missing from StringsEn.json: {string.Join(", ", missing)}");
    }

    [Fact]
    public void TheChineseOverlayOnlyContainsKeysEnglishAlsoDefines()
    {
        var english = ReadDictionary("StringsEn.json");
        var chinese = ReadDictionary("StringsZh.json");
        var stray = chinese.Keys.Where(k => !english.ContainsKey(k)).Order().ToArray();

        Assert.True(stray.Length == 0, $"keys in StringsZh.json with no English source: {string.Join(", ", stray)}");
    }

    [Fact]
    public void EveryReferencedKeyResolvesThroughTheLoadedDictionary()
    {
        // A key equal to its own value means the embedded dictionary did not load or the entry is
        // blank -- the exact silent failure a half-migrated string produces at runtime.
        var unresolved = ReferencedKeys().Where(k => Localizer.Loc(k) == k).Order().ToArray();

        Assert.True(unresolved.Length == 0, $"keys that did not resolve: {string.Join(", ", unresolved)}");
    }
}
