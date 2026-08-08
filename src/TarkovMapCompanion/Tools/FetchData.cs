using System.IO.Compression;
using System.Text.Json;
using TarkovMapCompanion.Data;

namespace TarkovMapCompanion.Tools;

/// <summary>
/// Regenerates the embedded POI snapshot from <c>json.tarkov.dev</c>.
/// </summary>
/// <remarks>
/// Run with <c>--fetch-data</c> and rebuild to refresh the data the app ships with. Kept as a mode
/// of the app rather than a separate script so it goes through the same fetch and parse code the
/// app uses -- a generator that can succeed where the app would fail is worse than no generator.
/// </remarks>
public static class FetchData
{
    private const string DefaultOutput = "src/TarkovMapCompanion/Data/Snapshots/mapdata.json.gz";

    public static async Task<int> RunAsync(string[] args)
    {
        var output = args.Length > 0 ? args[0] : DefaultOutput;

        Console.WriteLine($"source     {MapDataStore.DataUrl}");
        Console.WriteLine($"           {MapDataStore.TranslationsUrl}");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

        Data.Models.MapDataDocument document;
        try
        {
            document = await MapDataStore.FetchAsync(http).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fetch failed: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"maps       {document.Maps.Count}");
        Console.WriteLine($"strings    {document.Translations.Count}");

        var extracts = document.Maps.Values.Sum(m => m.Extracts?.Count ?? 0);
        var spawns = document.Maps.Values.Sum(m => m.Spawns?.Count ?? 0);
        var loose = document.Maps.Values.Sum(m => m.LootContainers?.Count ?? 0);
        Console.WriteLine($"extracts   {extracts}");
        Console.WriteLine($"spawns     {spawns}");
        Console.WriteLine($"containers {loose}");

        MapDataStore.Slim(document);

        var directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using (var file = File.Create(output))
        await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        {
            await JsonSerializer.SerializeAsync(
                gzip,
                document,
                new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })
                .ConfigureAwait(false);
        }

        var size = new FileInfo(output).Length;
        Console.WriteLine($"wrote      {Path.GetFullPath(output)} ({size / 1024} KB gzipped)");
        Console.WriteLine("Rebuild to embed the new snapshot.");
        return 0;
    }
}
