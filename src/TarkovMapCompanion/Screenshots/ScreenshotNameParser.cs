using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Screenshots;

/// <summary>
/// Decodes the player transform Escape from Tarkov writes into its screenshot filenames.
/// </summary>
/// <remarks>
/// <para>The shape, confirmed against real captures:</para>
/// <code>
/// 2026-08-07[19-31]_-720.10, -48.62, 430.51_-0.03195, 0.66069, -0.02987, -0.74938_11.67 (0).png
/// |--date--||time-| |------ x, y, z -----|  |------- qx, qy, qz, qw --------------| |----| |-|
/// </code>
/// <list type="bullet">
///   <item><description><c>x, y, z</c> is the Unity world position; <c>y</c> is height.</description></item>
///   <item><description><c>qx..qw</c> is the camera orientation quaternion.</description></item>
///   <item><description>The lone float is the in-raid clock in hours (11.67 is 11:40).</description></item>
///   <item><description><c>(0)</c> is the duplicate-name counter Windows-style, not always present.</description></item>
/// </list>
/// </remarks>
public static partial class ScreenshotNameParser
{
    /// <summary>
    /// Deliberately tolerant about whitespace and the trailing counter, and deliberately strict
    /// about the field layout: a name that is nearly right is far more likely to be some other
    /// tool's file than a fix worth plotting.
    /// </summary>
    [GeneratedRegex(
        """
        ^(?<date>\d{4}-\d{2}-\d{2})
        \[(?<hour>\d{1,2})-(?<minute>\d{2})(?:-(?<second>\d{2}))?\]
        _(?<x>-?\d+(?:\.\d+)?),\s*(?<y>-?\d+(?:\.\d+)?),\s*(?<z>-?\d+(?:\.\d+)?)
        _(?<qx>-?\d+(?:\.\d+)?),\s*(?<qy>-?\d+(?:\.\d+)?),\s*(?<qz>-?\d+(?:\.\d+)?),\s*(?<qw>-?\d+(?:\.\d+)?)
        _(?<raidTime>-?\d+(?:\.\d+)?)
        (?:\s*\(\d+\))?$
        """,
        RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex NamePatternRegex();

    private static Regex NamePattern => NamePatternRegex();

    /// <summary>Extensions the game is known to write. Anything else is not ours to touch.</summary>
    private static readonly string[] KnownExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary>
    /// True when a file looks like a Tarkov screenshot. Used as a safety gate before deleting
    /// anything, so it must not be loosened to "any image in the folder".
    /// </summary>
    public static bool IsScreenshotFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var extension = Path.GetExtension(path);
        if (!KnownExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return false;

        return NamePattern.IsMatch(Path.GetFileNameWithoutExtension(path));
    }

    public static bool TryParse(string path, [NotNullWhen(true)] out PlayerFix? fix)
    {
        fix = null;

        if (string.IsNullOrWhiteSpace(path))
            return false;

        var extension = Path.GetExtension(path);
        if (!KnownExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return false;

        var match = NamePattern.Match(Path.GetFileNameWithoutExtension(path));
        if (!match.Success)
            return false;

        if (!TryNumber(match, "x", out var x) ||
            !TryNumber(match, "y", out var y) ||
            !TryNumber(match, "z", out var z) ||
            !TryNumber(match, "qx", out var qx) ||
            !TryNumber(match, "qy", out var qy) ||
            !TryNumber(match, "qz", out var qz) ||
            !TryNumber(match, "qw", out var qw) ||
            !TryNumber(match, "raidTime", out var raidTime))
        {
            return false;
        }

        if (!TryTimestamp(match, out var takenAt))
            return false;

        fix = new PlayerFix
        {
            Position = new GamePosition(x, y, z),
            Rotation = (qx, qy, qz, qw),
            YawDegrees = YawFromQuaternion(qx, qy, qz, qw),
            RaidTimeHours = raidTime,
            TakenAt = takenAt,
            FilePath = path,
        };

        return true;
    }

    /// <summary>
    /// Yaw about the vertical axis, in degrees clockwise from +Z, so that a heading of
    /// <c>y</c> corresponds to the direction <c>(sin y, cos y)</c> in game (x, z).
    /// </summary>
    /// <remarks>
    /// Standard Unity quaternion-to-Euler for the Y component. Verified against two consecutive
    /// real captures: the pair yields 202.8 degrees, direction (-0.39, -0.92), against an actual
    /// displacement of (-0.41, -5.31) -- a player walking forward. Also handles the double-cover
    /// case (q and -q describing the same orientation) correctly, which matters because the game
    /// writes both signs.
    /// </remarks>
    public static double YawFromQuaternion(double qx, double qy, double qz, double qw)
    {
        var siny = 2.0 * (qw * qy + qx * qz);
        var cosy = 1.0 - 2.0 * (qy * qy + qz * qz);
        return MapProjection.Normalize360(Math.Atan2(siny, cosy) * 180.0 / Math.PI);
    }

    private static bool TryNumber(Match match, string group, out double value) =>
        double.TryParse(match.Groups[group].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryTimestamp(Match match, out DateTime timestamp)
    {
        timestamp = default;

        if (!DateTime.TryParseExact(
                match.Groups["date"].Value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return false;
        }

        if (!int.TryParse(match.Groups["hour"].Value, out var hour) ||
            !int.TryParse(match.Groups["minute"].Value, out var minute))
        {
            return false;
        }

        var secondGroup = match.Groups["second"];
        var second = secondGroup.Success && int.TryParse(secondGroup.Value, out var parsed) ? parsed : 0;

        if (hour > 23 || minute > 59 || second > 59)
            return false;

        timestamp = date.AddHours(hour).AddMinutes(minute).AddSeconds(second);
        return true;
    }
}
