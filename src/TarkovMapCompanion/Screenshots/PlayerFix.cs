using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Screenshots;

/// <summary>
/// One position report, decoded from the name of a screenshot the game wrote.
/// </summary>
/// <remarks>
/// Nothing here comes from the running game: Escape from Tarkov puts the player's transform into
/// the filename itself, and reading a filename is the whole of this app's input.
/// </remarks>
public sealed record PlayerFix
{
    public required GamePosition Position { get; init; }

    /// <summary>Camera yaw in degrees, clockwise from +Z. See <see cref="ScreenshotNameParser"/>.</summary>
    public required double YawDegrees { get; init; }

    /// <summary>Raw quaternion from the filename, kept for diagnostics.</summary>
    public required (double X, double Y, double Z, double W) Rotation { get; init; }

    /// <summary>In-raid clock at the moment of the screenshot, as hours past midnight.</summary>
    public required double RaidTimeHours { get; init; }

    /// <summary>Wall-clock timestamp the game stamped into the name. Local time, no zone info.</summary>
    public required DateTime TakenAt { get; init; }

    public required string FilePath { get; init; }

    public string FileName => Path.GetFileName(FilePath);

    /// <summary>The in-raid clock formatted the way the game shows it, e.g. <c>11:40</c>.</summary>
    public string RaidTimeDisplay
    {
        get
        {
            var totalMinutes = (int)Math.Round(RaidTimeHours * 60.0);
            var hours = (totalMinutes / 60) % 24;
            var minutes = ((totalMinutes % 60) + 60) % 60;
            return $"{hours:00}:{minutes:00}";
        }
    }
}
