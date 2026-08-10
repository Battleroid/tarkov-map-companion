using SkiaSharp;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// Colors for everything drawn on top of the map.
/// </summary>
/// <remarks>
/// <para>
/// These are chosen against the map artwork, not against the app chrome, so they do not come from
/// the theme tokens. The tarkov.dev maps are dark teal, gray and sand in both themes, so one
/// palette works for light and dark; only the halo color changes.
/// </para>
/// <para>
/// The categorical hues are separated in lightness as well as hue, so they stay distinguishable
/// under deuteranopia and protanopia -- the extract faction colors in particular, since picking
/// the wrong extract because two markers looked alike is a real cost.
/// </para>
/// </remarks>
public static class MarkerPalette
{
    // ---- Player -------------------------------------------------------------

    /// <summary>The default player color. Overridable per user; see <c>PlayerOverlay.Color</c>.</summary>
    public static readonly SKColor Player = new(0xF5, 0xC9, 0x42);

    public static readonly SKColor PlayerOutline = new(0x10, 0x10, 0x10);

    // ---- Extract factions ---------------------------------------------------

    /// <summary>PMC only. Warm and light.</summary>
    public static readonly SKColor ExtractPmc = new(0x5C, 0xD6, 0x5C);

    /// <summary>Scav only. Distinct in lightness from PMC, not just hue.</summary>
    public static readonly SKColor ExtractScav = new(0xFF, 0x8A, 0x3D);

    /// <summary>Usable by both, which includes the co-op extracts.</summary>
    public static readonly SKColor ExtractShared = new(0x4F, 0xC3, 0xF7);

    /// <summary>Transit to another map.</summary>
    public static readonly SKColor Transit = new(0xC9, 0x8A, 0xFF);

    // ---- Other POIs ---------------------------------------------------------

    public static readonly SKColor Spawn = new(0xE0, 0x6C, 0x75);
    public static readonly SKColor BossZone = new(0xFF, 0x4D, 0x4D);
    public static readonly SKColor Loot = new(0xD8, 0xA6, 0x57);
    public static readonly SKColor Hazard = new(0xFF, 0x5C, 0x33);
    public static readonly SKColor Lock = new(0xB0, 0xBE, 0xC5);
    public static readonly SKColor Switch = new(0x9C, 0xCC, 0x65);
    public static readonly SKColor StationaryWeapon = new(0x90, 0xA4, 0xAE);
    public static readonly SKColor BtrStop = new(0x80, 0xCB, 0xC4);

    // ---- Chrome drawn onto the map -----------------------------------------

    public static readonly SKColor Halo = new(0x00, 0x00, 0x00, 0xC0);
    public static readonly SKColor LabelText = new(0xFF, 0xFF, 0xFF);

    /// <summary>The line from the player to the selected extract.</summary>
    public static readonly SKColor ExtractLine = new(0xF5, 0xC9, 0x42);

    // ---- Route the player drew ----------------------------------------------

    /// <summary>
    /// Waypoint pins. Deliberately unlike every extract faction color: these are the player's own
    /// marks, and confusing one for an exit would be the worst possible mix-up.
    /// </summary>
    public static readonly SKColor Waypoint = new(0xFF, 0x4F, 0x9A);

    /// <summary>A waypoint that has been reached and is about to disappear.</summary>
    public static readonly SKColor WaypointVisited = new(0x6E, 0x7B, 0x8A);

    public static readonly SKColor WaypointLabel = new(0xFF, 0xFF, 0xFF);

    // ---- Quests -------------------------------------------------------------

    /// <summary>
    /// Tracked quest objectives.
    /// </summary>
    /// <remarks>
    /// One color for all of them, with a number in the marker where a task has several objectives
    /// on one map. A color per task was the alternative and it does not survive contact with the
    /// data: Lighthouse can have a dozen tracked objectives at once, which is more distinguishable
    /// hues than exist once the exits, the squad and the player's own marker have taken theirs.
    /// </remarks>
    public static readonly SKColor Quest = new(0x8A, 0xB4, 0xFF);

    // ---- Squad --------------------------------------------------------------

    /// <summary>
    /// One color per squad slot, assigned by position in the roster so a teammate keeps the same
    /// color all raid. Chosen to avoid the player's yellow, the marker pink, and the extract
    /// faction colors, so a teammate can never be mistaken for an exit.
    /// </summary>
    public static readonly SKColor[] PeerColors =
    [
        new(0x64, 0xB5, 0xF6),
        new(0x81, 0xC7, 0x84),
        new(0xBA, 0x68, 0xC8),
        new(0x4D, 0xB6, 0xAC),
        new(0xF0, 0x6E, 0x8C),
    ];

    /// <summary>
    /// Dashed ring marking an exit with conditions attached. Deliberately a shape cue rather than
    /// only a color one, since the faction colors already use the hue channel.
    /// </summary>
    public static readonly SKColor ConditionalRing = new(0xFF, 0xD5, 0x4F, 0xE0);

    // ---- Colors a user can pick ---------------------------------------------

    /// <summary>
    /// The colors offered for your own marker and for the guide line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A closed set rather than a free picker, for three reasons. It keeps the lightness separation
    /// this file promises, which an arbitrary hue would throw away. It makes "somebody in your squad
    /// already has that one" a question the picker can answer. And two players choosing colors four
    /// hex digits apart is a worse outcome than either of them not getting their exact favorite.
    /// </para>
    /// <para>
    /// Named because the roster and the picker both have to talk about them, and "the blue one" is
    /// how people actually refer to a teammate's marker.
    /// </para>
    /// </remarks>
    public static readonly (string Name, SKColor Color)[] PlayerChoices =
    [
        ("Amber", Player),
        ("Sky", PeerColors[0]),
        ("Moss", PeerColors[1]),
        ("Orchid", PeerColors[2]),
        ("Teal", PeerColors[3]),
        ("Rose", PeerColors[4]),
        ("Ice", new SKColor(0xE0, 0xE6, 0xED)),
        ("Rust", new SKColor(0xE2, 0x72, 0x3B)),
        ("Lime", new SKColor(0xC6, 0xE3, 0x4A)),
        ("Violet", new SKColor(0x8C, 0x7A, 0xE6)),
    ];

    /// <summary>Applied to markers on a floor other than the one being viewed.</summary>
    public static SKColor Dimmed(SKColor color) => color.WithAlpha(0x50);
}
