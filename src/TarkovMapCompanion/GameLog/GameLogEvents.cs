namespace TarkovMapCompanion.GameLog;

/// <summary>The things worth noticing in Tarkov's own log.</summary>
public enum GameLogEventKind
{
    /// <summary>
    /// The game began loading a map. The earliest signal there is, and by some way: measured
    /// against real logs it lands 20 seconds to two minutes before the player has control.
    /// </summary>
    ScenePreset,

    /// <summary>
    /// The raid's server assignment, carrying the authoritative location id. Later than
    /// <see cref="ScenePreset"/> and worth waiting for, because it is the one that cannot be wrong.
    /// </summary>
    RaidCreated,

    /// <summary>The player has control. This is where a raid actually starts.</summary>
    RaidStarted,

    /// <summary>
    /// The profile reloaded, which is what returning to the menu looks like from out here.
    /// </summary>
    /// <remarks>
    /// Inferred rather than announced. Tarkov writes no "raid over" line, and this one also fires
    /// on the way in, so it only means anything after a <see cref="RaidStarted"/>.
    /// </remarks>
    MenuReturned,
}

/// <summary>One recognized line, decoded.</summary>
/// <remarks>
/// <see cref="MapTokens"/> is a list rather than a single name because the scene-preset line offers
/// two different names for the same map and neither is reliably the one tarkov.dev records. Both are
/// handed on and the catalog decides; see <c>MapNameIds</c> for the measurements behind that.
/// </remarks>
public sealed record GameLogEvent
{
    public required GameLogEventKind Kind { get; init; }

    /// <summary>When the game wrote the line, in local time. Null if the stamp was unreadable.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>Candidate names for the map, best first. Empty for events that carry no map.</summary>
    public IReadOnlyList<string> MapTokens { get; init; } = [];

    /// <summary><c>Online</c> or <c>Pve</c>, when the line says. Recorded, not acted on.</summary>
    public string? RaidMode { get; init; }

    /// <summary>The line it came from, so diagnostics can show its work.</summary>
    public string Line { get; init; } = "";
}
