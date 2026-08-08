using TarkovMapCompanion.Screenshots;

namespace TarkovMapCompanion.Tools;

/// <summary>
/// Prints every place Tarkov screenshots might be and what is in each.
/// </summary>
/// <remarks>
/// "The map never moves" is the hardest thing to diagnose remotely, because the app looks perfectly
/// healthy while watching a folder nothing is ever written to. This turns a conversation into one
/// pasted block of output.
/// </remarks>
public static class FindScreenshots
{
    public static int Run()
    {
        var candidates = ScreenshotFolders.Candidates();

        Console.WriteLine("Looking for Escape from Tarkov screenshots...");
        Console.WriteLine();

        foreach (var candidate in candidates)
        {
            var state = candidate switch
            {
                { Looks: true } => $"{candidate.ScreenshotCount} screenshots",
                { Exists: true } => "exists, but empty",
                _ => "not there",
            };

            Console.WriteLine($"  [{state,-18}] {candidate.Path}");
            Console.WriteLine($"   {"",-20} via {candidate.Source}");
        }

        Console.WriteLine();

        var best = candidates.FirstOrDefault(c => c.Looks);

        if (best is not null)
        {
            Console.WriteLine($"Use this one: {best.Path}");
            Console.WriteLine("Set it under Settings if it is not already what the app is watching.");
            return 0;
        }

        Console.WriteLine("No Tarkov screenshots found in any of those.");
        Console.WriteLine();
        Console.WriteLine("Either the game has not written any yet, or it is writing somewhere else.");
        Console.WriteLine("Take a screenshot in raid, find the PNG, and point Settings at the folder");
        Console.WriteLine("it landed in. The names look like:");
        Console.WriteLine("  2026-08-08[14-41]_569.2, 2.9, -54.6_0, 0.77, 0, 0.64_13.83 (0).png");

        return 1;
    }
}
