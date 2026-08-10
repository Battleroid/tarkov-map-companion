using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovMapCompanion.Data;

namespace TarkovMapCompanion.Tools;

/// <summary>
/// Regenerates the embedded quest snapshot from <c>json.tarkov.dev</c>.
/// </summary>
/// <remarks>
/// Modeled on <see cref="FetchData"/>, including the rule that matters: the projection lives in
/// <see cref="TaskStore.FetchAsync"/>, so this cannot succeed in a way the app's own refresh would
/// not.
/// </remarks>
public static class FetchTasks
{
    private const string DefaultOutput = "src/TarkovMapCompanion/Data/Snapshots/tasks.json.gz";

    public static async Task<int> RunAsync(string[] args)
    {
        var output = args.Length > 0 ? args[0] : DefaultOutput;

        Console.WriteLine($"source     {TaskStore.DataUrl}");
        Console.WriteLine($"           {TaskStore.TranslationsUrl}");
        Console.WriteLine($"           {TaskStore.TradersUrl}");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

        Data.Models.TaskDocument document;
        try
        {
            document = await TaskStore.FetchAsync(http).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fetch failed: {ex.Message}");
            return 1;
        }

        var objectives = document.Tasks.Sum(t => t.Objectives.Count);
        var points = document.Tasks.Sum(t => t.Objectives.Sum(o => o.Points.Count));
        var placed = document.Tasks.Count(t => t.Objectives.Any(o => o.Points.Count > 0));

        Console.WriteLine($"tasks      {document.Tasks.Count}");
        Console.WriteLine($"objectives {objectives}");
        Console.WriteLine($"positions  {points}");
        Console.WriteLine($"on a map   {placed} tasks");
        Console.WriteLine($"traders    {document.Tasks.Select(t => t.Trader).Distinct().Count()}");

        // A name that did not resolve comes through as its own localization key, which is a
        // 24-character hex blob and unmistakable. Worth counting rather than shipping quietly.
        var unresolved = document.Tasks.Count(t => t.Name.EndsWith(" name", StringComparison.Ordinal));
        if (unresolved > 0)
            Console.Error.WriteLine($"warning:   {unresolved} task names did not resolve to English");

        var directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using (var file = File.Create(output))
        await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        {
            await JsonSerializer.SerializeAsync(
                gzip,
                document,
                new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull })
                .ConfigureAwait(false);
        }

        var size = new FileInfo(output).Length;
        Console.WriteLine($"wrote      {Path.GetFullPath(output)} ({size / 1024} KB gzipped)");
        Console.WriteLine("Rebuild to embed the new snapshot.");
        return 0;
    }
}
