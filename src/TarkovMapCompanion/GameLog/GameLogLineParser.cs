using System.Globalization;
using System.Text.RegularExpressions;

namespace TarkovMapCompanion.GameLog;

/// <summary>
/// Turns one line of Tarkov's <c>application_*.log</c> into an event, or nothing.
/// </summary>
/// <remarks>
/// <para>
/// Pure by design. Everything interesting about reading the log is decided here, where it can be
/// tested against lines captured verbatim from real logs rather than against a live game.
/// </para>
/// <para>
/// The log is chatty: a single raid produces tens of thousands of lines, of which four matter. So
/// the cheap rejection comes first and the regex work only happens for lines that already look
/// like a candidate.
/// </para>
/// </remarks>
public static partial class GameLogLineParser
{
    /// <summary>Format of the stamp every line begins with.</summary>
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>
    /// Reads one line. Returns null for the overwhelming majority of them.
    /// </summary>
    public static GameLogEvent? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        // Ordered by how specific the match is, not by how often it fires. A raid-created line also
        // contains the word "Location", and a scene line also contains "maps/".
        if (line.Contains("scene preset path:", StringComparison.OrdinalIgnoreCase))
            return ParseScenePreset(line);

        if (line.Contains("profileStatus:", StringComparison.OrdinalIgnoreCase))
            return ParseRaidCreated(line);

        if (line.Contains("|GameStarted:", StringComparison.Ordinal))
            return new GameLogEvent { Kind = GameLogEventKind.RaidStarted, At = ReadTimestamp(line), Line = line };

        if (line.Contains("|CompleteSelectedProfile", StringComparison.Ordinal))
        {
            return new GameLogEvent
            {
                Kind = GameLogEventKind.MenuReturned,
                At = ReadTimestamp(line),
                ProfileId = ReadProfileId(line),
                Line = line,
            };
        }

        // The other half of the same pair, and the one that means "from here on, the trader
        // messages belong to this character". Switching between PVE and PVP is a profile load
        // rather than a restart, and nothing in a trader message says who it was addressed to.
        if (line.Contains("|PrepareSelectedProfileLocally", StringComparison.Ordinal))
        {
            return new GameLogEvent
            {
                Kind = GameLogEventKind.ProfileLoaded,
                At = ReadTimestamp(line),
                ProfileId = ReadProfileId(line),
                Line = line,
            };
        }

        return null;
    }

    /// <summary>
    /// Decodes the line the game writes as it starts loading a map.
    /// </summary>
    /// <remarks>
    /// Both names are kept. Real logs show them disagreeing in both directions: Customs writes
    /// <c>customs_preset.bundle</c> with <c>rcid:bigmap</c>, where <c>bigmap</c> is the id
    /// tarkov.dev knows; Ground Zero's tutorial writes <c>sandbox_start_preset.bundle</c> with
    /// <c>rcid:Sandbox_SL</c>, where this time it is the bundle that matches. Picking one would be
    /// picking which maps to fail on.
    /// </remarks>
    private static GameLogEvent? ParseScenePreset(string line)
    {
        var match = ScenePresetPattern().Match(line);
        if (!match.Success)
            return null;

        var rcid = match.Groups["rcid"].Value;
        var bundle = StripPresetSuffix(match.Groups["bundle"].Value);

        var tokens = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(rcid))
            tokens.Add(rcid);

        if (!string.IsNullOrWhiteSpace(bundle) && !string.Equals(bundle, rcid, StringComparison.OrdinalIgnoreCase))
            tokens.Add(bundle);

        if (tokens.Count == 0)
            return null;

        return new GameLogEvent
        {
            Kind = GameLogEventKind.ScenePreset,
            At = ReadTimestamp(line),
            MapTokens = tokens,
            Line = line,
        };
    }

    private static GameLogEvent? ParseRaidCreated(string line)
    {
        var location = LocationPattern().Match(line);
        if (!location.Success)
            return null;

        var token = location.Groups["map"].Value.Trim();
        if (token.Length == 0)
            return null;

        var mode = RaidModePattern().Match(line);

        return new GameLogEvent
        {
            Kind = GameLogEventKind.RaidCreated,
            At = ReadTimestamp(line),
            MapTokens = [token],
            RaidMode = mode.Success ? mode.Groups["mode"].Value.Trim() : null,
            Line = line,
        };
    }

    /// <summary>
    /// The leading timestamp, in local time.
    /// </summary>
    /// <remarks>
    /// Local, because the game writes local and says nothing about a zone. Reading it as UTC would
    /// make every event look hours old, which is exactly the sort of thing that would silently
    /// disable a freshness check somewhere downstream.
    /// </remarks>
    public static DateTimeOffset? ReadTimestamp(string line)
    {
        if (line.Length < TimestampFormat.Length)
            return null;

        return DateTime.TryParseExact(
            line.AsSpan(0, TimestampFormat.Length),
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed))
            : null;
    }

    /// <summary>
    /// Turns <c>customs_preset</c> into <c>customs</c>, and leaves <c>shopping_mall</c> alone.
    /// </summary>
    private static string StripPresetSuffix(string bundle) =>
        bundle.EndsWith("_preset", StringComparison.OrdinalIgnoreCase)
            ? bundle[..^"_preset".Length]
            : bundle;

    // The bundle name stops at ".bundle"; the rcid stops at the first dot, which is where
    // ".ScenesPreset.asset" begins. Case varies between maps, hence IgnoreCase on both.
    [GeneratedRegex(
        @"scene preset path:maps/(?<bundle>[^\s|]+?)\.bundle\s+rcid:(?<rcid>[^.\s|]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScenePresetPattern();

    // Inside a quoted, comma-separated status blob, so the value runs to the next comma.
    [GeneratedRegex(
        @"\bLocation:\s*(?<map>[^,']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocationPattern();

    [GeneratedRegex(
        @"\bRaidMode:\s*(?<mode>[^,']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RaidModePattern();

    /// <summary>The profile id out of a profile-select line, or null.</summary>
    private static string? ReadProfileId(string line) =>
        ProfileIdPattern().Match(line) is { Success: true } match ? match.Groups["id"].Value : null;

    [GeneratedRegex(
        @"ProfileId:(?<id>[0-9a-f]{24})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProfileIdPattern();
}
