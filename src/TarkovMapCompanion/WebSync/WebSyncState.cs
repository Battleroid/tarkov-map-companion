using System.Text.Json.Serialization;

namespace TarkovMapCompanion.WebSync;

// ---------------------------------------------------------------------------
// Coordinate rule for the whole WebSync wire format.
//
// Every point in this file is a GAME coordinate pair {x, z} -- the same space
// ScreenshotNameParser and MapProjection.ToBase consume -- unless the type is
// explicitly named "...Base...". Game coordinates are map-independent, so a
// client can hold a route or a trail across a map change without invalidating
// anything.
//
// The single exception is extract.guideBase, which is a BASE pixel-space point.
// The guide-line maths (waypoint-vs-exit precedence, bearing, distance) is too
// tangled to re-derive client-side, so the server hands the browser the one
// result it cannot cheaply recompute: the endpoint of the line, already
// projected. Nothing else on the wire is in base space.
// ---------------------------------------------------------------------------

/// <summary>
/// The read-only LAN state mirror's full JSON snapshot, served from <c>GET /api/state</c> and sent
/// as the first frame of every WebSocket connection.
/// </summary>
/// <remarks>
/// A missing field means <c>null</c> or an empty array, never an error: the browser client is
/// expected to tolerate unknown and absent fields, so this shape can grow additively without a
/// version negotiation.
/// </remarks>
internal sealed record WebSyncSnapshot
{
    /// <summary>The schema version this build emits. Bump only for a breaking shape change.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Schema version, currently <see cref="CurrentVersion"/>.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>When the snapshot was built, UTC, serialized ISO-8601 with a trailing <c>Z</c>.</summary>
    public DateTime GeneratedAt { get; init; }

    /// <summary>True while a screenshot has placed the player in a live raid.</summary>
    public bool InRaid { get; init; }

    /// <summary>The current map's description, or null when no map is selected.</summary>
    public WebSyncMap? Map { get; init; }

    /// <summary>The local player's position and heading, or null before the first fix.</summary>
    public WebSyncPlayer? Player { get; init; }

    /// <summary>The active guide line, or null when there is no extract or waypoint target.</summary>
    public WebSyncExtract? Extract { get; init; }

    /// <summary>The player's own route pins, in order, in game coordinates.</summary>
    public List<WebSyncWaypoint> Waypoints { get; init; } = [];

    /// <summary>Teammates' routes produced by this desktop app, each already colored.</summary>
    public List<WebSyncRouteOwner> SharedRoutes { get; init; } = [];

    /// <summary>Teammates' routes as relayed by the party session. Carries no color; see the type.</summary>
    public List<WebSyncPeerRoute> PeerRoutes { get; init; } = [];

    /// <summary>Every point of interest on the current map, already localized.</summary>
    public List<WebSyncPoi> Pois { get; init; } = [];

    /// <summary>Every tracked quest objective mark on the current map.</summary>
    public List<WebSyncQuest> Quests { get; init; } = [];

    /// <summary>Every map note on the current map.</summary>
    public List<WebSyncAnnotation> Annotations { get; init; } = [];

    /// <summary>Every party member the session currently knows about.</summary>
    public List<WebSyncPeer> Peers { get; init; } = [];

    /// <summary>Recent squad pings.</summary>
    public List<WebSyncPing> Pings { get; init; } = [];

    /// <summary>Client-facing behavior toggles mirrored from the desktop settings.</summary>
    public WebSyncSettings? Settings { get; init; }
}

/// <summary>
/// The current map's description: enough for the browser to draw its imagery and reproject game
/// coordinates without knowing anything about tarkov.dev.
/// </summary>
internal sealed record WebSyncMap
{
    /// <summary>Map key, matching <c>MapCatalog</c>'s key and the <c>{mapId}</c> URL segment.</summary>
    public string Key { get; init; } = "";

    /// <summary>Already-localized map name for display.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>How to fetch the base imagery: an SVG or a tile set.</summary>
    public WebSyncImagery? Imagery { get; init; }

    /// <summary>Coefficients to reproduce <c>MapProjection.ToBase</c> in JavaScript.</summary>
    public WebSyncProjection? Projection { get; init; }

    /// <summary>Base-space rect the map imagery covers.</summary>
    public WebSyncRect? BaseRect { get; init; }

    /// <summary>Base-space rect the SVG overlay is stretched to; differs from <see cref="BaseRect"/> on Reserve.</summary>
    public WebSyncRect? SvgBaseRect { get; init; }

    /// <summary>The map's floors, in source order.</summary>
    public List<WebSyncFloor> Floors { get; init; } = [];

    /// <summary>Floor names currently active on the desktop, for the client's initial view.</summary>
    public List<string> ActiveFloors { get; init; } = [];
}

/// <summary>
/// Base imagery descriptor. A discriminated union on <see cref="Type"/>: <c>svg</c> uses
/// <see cref="Url"/>, <c>tiles</c> uses <see cref="UrlTemplate"/> and the zoom fields.
/// </summary>
internal sealed record WebSyncImagery
{
    /// <summary>Either <c>"svg"</c> or <c>"tiles"</c>.</summary>
    public string Type { get; init; } = "";

    /// <summary>SVG URL, for <c>type == "svg"</c>.</summary>
    public string? Url { get; init; }

    /// <summary>Tile URL template with <c>{z}/{x}/{y}</c> placeholders, for <c>type == "tiles"</c>.</summary>
    public string? UrlTemplate { get; init; }

    /// <summary>Tile edge length in pixels, for <c>type == "tiles"</c>.</summary>
    public int? TileSize { get; init; }

    /// <summary>Lowest zoom level the tile set provides.</summary>
    public int? MinZoom { get; init; }

    /// <summary>Highest zoom level the tile set provides.</summary>
    public int? MaxZoom { get; init; }
}

/// <summary>
/// The six scalars plus rotation that reproduce <c>MapProjection.ToBase</c> exactly.
/// </summary>
/// <remarks>
/// <para>
/// This is <b>not</b> a general 2x3 affine matrix. It is rotate-then-scale, in this exact order:
/// </para>
/// <code>
///   rx = x*cos - z*sin;
///   ry = x*sin + z*cos;
///   baseX = a*rx + b;
///   baseY = -c*ry + d;
/// </code>
/// <para>
/// There is deliberately no <c>tx</c>/<c>ty</c>: <see cref="B"/> and <see cref="D"/> already are the
/// translation terms. Note the negation on the Y axis, which mirrors Leaflet's downward-growing
/// pixel Y. The browser client ports the four lines above verbatim.
/// </para>
/// </remarks>
internal sealed record WebSyncProjection
{
    /// <summary>Cosine of the map's coordinate rotation.</summary>
    public double Cos { get; init; }

    /// <summary>Sine of the map's coordinate rotation.</summary>
    public double Sin { get; init; }

    /// <summary>Scale on the rotated X axis.</summary>
    public double A { get; init; }

    /// <summary>Translation on the rotated X axis.</summary>
    public double B { get; init; }

    /// <summary>Scale on the rotated Z axis, negated when applied to Y.</summary>
    public double C { get; init; }

    /// <summary>Translation on the rotated Z axis.</summary>
    public double D { get; init; }

    /// <summary>The map's coordinate rotation in degrees, for client-side sanity checks.</summary>
    public double CoordinateRotationDegrees { get; init; }
}

/// <summary>An axis-aligned rectangle in base pixel space, as <c>{x, y, w, h}</c>.</summary>
internal sealed record WebSyncRect
{
    /// <summary>Left edge.</summary>
    public double X { get; init; }

    /// <summary>Top edge.</summary>
    public double Y { get; init; }

    /// <summary>Width.</summary>
    public double W { get; init; }

    /// <summary>Height.</summary>
    public double H { get; init; }
}

/// <summary>One floor of the current map, by its display name.</summary>
internal sealed record WebSyncFloor
{
    /// <summary>The floor's display name, e.g. <c>Ground_Level</c>. Not the SVG layer id.</summary>
    public string Name { get; init; } = "";
}

/// <summary>The local player's position and heading, in game coordinates.</summary>
internal sealed record WebSyncPlayer
{
    /// <summary>Game X.</summary>
    public double X { get; init; }

    /// <summary>Game Z.</summary>
    public double Z { get; init; }

    /// <summary>Raw game yaw, clockwise from +Z, as parsed from the screenshot name.</summary>
    public double YawDegrees { get; init; }

    /// <summary>
    /// Precomputed clockwise screen rotation for the player marker, in degrees.
    /// </summary>
    /// <remarks>
    /// The client MUST NOT recompute this from <see cref="YawDegrees"/>.
    /// <c>MapProjection.ScreenAngleDegrees</c> adds a further 180 degrees when the map's coordinate
    /// rotation is not a multiple of 180, a step that has no place in a naive yaw-plus-rotation
    /// port. Drawing the marker with this value keeps it pointing the same way the desktop does;
    /// deriving it client-side reverses the marker on rotated maps.
    /// </remarks>
    public double ScreenAngleDegrees { get; init; }

    /// <summary>Name of the floor the player is on, or null when it could not be determined.</summary>
    public string? Floor { get; init; }

    /// <summary>Recent player positions, oldest first, in game coordinates.</summary>
    public List<WebSyncPoint> Trail { get; init; } = [];
}

/// <summary>A point in game coordinates, <c>{x, z}</c>.</summary>
internal sealed record WebSyncPoint
{
    /// <summary>Game X.</summary>
    public double X { get; init; }

    /// <summary>Game Z.</summary>
    public double Z { get; init; }
}

/// <summary>
/// The active guide line: the selected extract or waypoint the desktop is routing to.
/// </summary>
internal sealed record WebSyncExtract
{
    /// <summary>Stable id of the target, when it has one. May be null for a waypoint target.</summary>
    public string? TargetId { get; init; }

    /// <summary>Already-localized display name of the target.</summary>
    public string? TargetName { get; init; }

    /// <summary>
    /// Endpoint of the guide line in BASE pixel space.
    /// </summary>
    /// <remarks>
    /// The one base-space value on the wire. The guide line's target selection and bearing maths are
    /// complex enough that reproducing them client-side would invite drift, so the projected
    /// endpoint is handed over ready to draw.
    /// </remarks>
    public WebSyncBasePoint? GuideBase { get; init; }

    /// <summary>Straight-line ground distance to the target in meters, or null before placement.</summary>
    public double? DistanceMeters { get; init; }

    /// <summary>Degrees to turn to face the target; negative is left, positive is right.</summary>
    public double? RelativeBearingDegrees { get; init; }
}

/// <summary>A point in base pixel space (<c>{x, y}</c>), used only by <see cref="WebSyncExtract.GuideBase"/>.</summary>
internal sealed record WebSyncBasePoint
{
    /// <summary>Base-space X.</summary>
    public double X { get; init; }

    /// <summary>Base-space Y.</summary>
    public double Y { get; init; }
}

/// <summary>One pin of the player's own route, in game coordinates.</summary>
internal sealed record WebSyncWaypoint
{
    /// <summary>Position in the route, from 1.</summary>
    public int N { get; init; }

    /// <summary>Game X.</summary>
    public double X { get; init; }

    /// <summary>Game Z.</summary>
    public double Z { get; init; }

    /// <summary>True once the pin has been reached and is awaiting removal.</summary>
    public bool Visited { get; init; }
}

/// <summary>
/// A teammate's route produced by this desktop app, in the owner's chosen color.
/// </summary>
/// <remarks>
/// Distinct from <see cref="WebSyncPeerRoute"/> on purpose: this comes from
/// <c>WaypointOverlay.SharedRoute</c> and already carries a color, whereas the party relay below
/// does not. Do not collapse the two.
/// </remarks>
internal sealed record WebSyncRouteOwner
{
    /// <summary>Display name of the player who owns the route.</summary>
    public string Owner { get; init; } = "";

    /// <summary>The owner's marker color as <c>#RRGGBB</c>.</summary>
    public string? Color { get; init; }

    /// <summary>The route's points, in visiting order, in game coordinates.</summary>
    public List<WebSyncPoint> Points { get; init; } = [];
}

/// <summary>
/// A teammate's route as relayed by the party session.
/// </summary>
/// <remarks>
/// This intentionally has no color field, and that is not an oversight: it comes from
/// <c>PartySession.Routes</c>, whose <c>PeerRoute</c> type carries no color. The browser client
/// colors it by matching <see cref="Name"/> against the corresponding <see cref="WebSyncPeer"/>'s
/// color. The colored shared routes above come from a different source and cannot be merged with
/// these.
/// </remarks>
internal sealed record WebSyncPeerRoute
{
    /// <summary>Display name of the player who owns the route.</summary>
    public string Name { get; init; } = "";

    /// <summary>The route's points, in visiting order, in game coordinates.</summary>
    public List<WebSyncPoint> Points { get; init; } = [];
}

/// <summary>One point of interest on the current map, already localized.</summary>
internal sealed record WebSyncPoi
{
    /// <summary>Stable id where the source provides one, else null.</summary>
    public string? Id { get; init; }

    /// <summary>
    /// The POI's layer name, a plain string from <c>PoiKind.ToString()</c>, e.g. <c>ExtractPmc</c>.
    /// Not validated; unknown values are the client's to ignore.
    /// </summary>
    public string Kind { get; init; } = "";

    /// <summary>Already-localized display name, produced by <c>Localizer.Pick</c> at the call site.</summary>
    public string Name { get; init; } = "";

    /// <summary>Game X.</summary>
    public double X { get; init; }

    /// <summary>Game Z.</summary>
    public double Z { get; init; }

    /// <summary>Whether the current raid offers this exit, or null when nobody has told us.</summary>
    public bool? Available { get; init; }

    /// <summary>True when using this requires a payment, item, switch, flare, partner or timing.</summary>
    public bool Conditional { get; init; }

    /// <summary>True when this is usable once per raid.</summary>
    public bool SingleUse { get; init; }

    /// <summary>Height band as <c>[bottom, top]</c>, or null when the source gives none.</summary>
    public List<double>? Elevation { get; init; }
}

/// <summary>One tracked quest objective mark on the current map, in game coordinates.</summary>
internal sealed record WebSyncQuest
{
    /// <summary>
    /// Flattened display text for the mark. The caller supplies it from <c>QuestMark</c>, preferring
    /// its <c>Description</c> and falling back to <c>TaskName</c>; this layer never calls the localizer.
    /// </summary>
    public string Label { get; init; } = "";

    /// <summary>Game X.</summary>
    public double X { get; init; }

    /// <summary>Game Z.</summary>
    public double Z { get; init; }
}

/// <summary>One map note on the current map, in game coordinates.</summary>
internal sealed record WebSyncAnnotation
{
    /// <summary>Stable local id, may be null if the caller has not assigned one.</summary>
    public string? Id { get; init; }

    /// <summary>Game X.</summary>
    public double X { get; init; }

    /// <summary>Game Z.</summary>
    public double Z { get; init; }

    /// <summary>The note's text.</summary>
    public string Text { get; init; } = "";

    /// <summary>Display name of who wrote it, or null when unknown.</summary>
    public string? Author { get; init; }
}

/// <summary>One party member as the session currently understands them, in game coordinates.</summary>
internal sealed record WebSyncPeer
{
    /// <summary>Their display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Game X.</summary>
    public double X { get; init; }

    /// <summary>Game Z.</summary>
    public double Z { get; init; }

    /// <summary>Their heading, clockwise from +Z.</summary>
    public double YawDegrees { get; init; }

    /// <summary>Their chosen marker color as <c>#RRGGBB</c>, or null when they have not picked one.</summary>
    public string? Color { get; init; }
}

/// <summary>A recent squad ping, in game coordinates.</summary>
internal sealed record WebSyncPing
{
    /// <summary>Game X.</summary>
    public double X { get; init; }

    /// <summary>Game Z.</summary>
    public double Z { get; init; }

    /// <summary>When the ping was placed, UTC, serialized ISO-8601 with a trailing <c>Z</c>.</summary>
    public DateTime At { get; init; }
}

/// <summary>Client-facing behavior flags mirrored from the desktop settings.</summary>
internal sealed record WebSyncSettings
{
    /// <summary>Whether the client should switch floors to follow the player automatically.</summary>
    public bool FollowPlayerFloor { get; init; }
}

/// <summary>
/// One incremental WebSocket message, discriminated on <see cref="Type"/>.
/// </summary>
/// <remarks>
/// <para>
/// Modelled as a single flattened envelope with a discriminator string and nullable payload
/// properties rather than an abstract base with derived records. The reason is source generation:
/// polymorphic serialization needs <c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c> wiring and
/// brings its own metadata rules, whereas a flat envelope is one registered type whose payloads are
/// ordinary init-only records. The server only ever serializes these, never deserializes them, so
/// the union's main benefit -- polymorphic read-back -- is unused here anyway. The browser switches
/// on <c>type</c> exactly as it would against a hierarchy.
/// </para>
/// <para>Message kinds and the payload they carry:</para>
/// <list type="bullet">
/// <item><description><c>player</c>: <see cref="Player"/>.</description></item>
/// <item><description><c>map</c>: <see cref="Snapshot"/>, the whole snapshot re-sent on a map change.</description></item>
/// <item><description><c>route</c>: <see cref="Waypoints"/>, <see cref="SharedRoutes"/>, <see cref="PeerRoutes"/>, <see cref="Extract"/>.</description></item>
/// <item><description><c>pois</c>: <see cref="Pois"/>, <see cref="Quests"/>.</description></item>
/// <item><description><c>meta</c>: <see cref="Annotations"/>, <see cref="Peers"/>, <see cref="Pings"/>.</description></item>
/// <item><description><c>raid</c>: <see cref="InRaid"/>.</description></item>
/// </list>
/// </remarks>
internal sealed record WebSyncMessage
{
    /// <summary>Discriminator: <c>player</c>, <c>map</c>, <c>route</c>, <c>pois</c>, <c>meta</c> or <c>raid</c>.</summary>
    public string Type { get; init; } = "";

    /// <summary>Payload for <c>type == "player"</c>.</summary>
    public WebSyncPlayer? Player { get; init; }

    /// <summary>Payload for <c>type == "map"</c>: the complete snapshot.</summary>
    public WebSyncSnapshot? Snapshot { get; init; }

    /// <summary>Payload for <c>type == "route"</c>: the player's route pins.</summary>
    public List<WebSyncWaypoint>? Waypoints { get; init; }

    /// <summary>Payload for <c>type == "route"</c>: colored shared routes.</summary>
    public List<WebSyncRouteOwner>? SharedRoutes { get; init; }

    /// <summary>Payload for <c>type == "route"</c>: party-relayed routes, colored client-side.</summary>
    public List<WebSyncPeerRoute>? PeerRoutes { get; init; }

    /// <summary>Payload for <c>type == "route"</c>: the active guide line, or null when there is none.</summary>
    public WebSyncExtract? Extract { get; init; }

    /// <summary>Payload for <c>type == "pois"</c>: points of interest.</summary>
    public List<WebSyncPoi>? Pois { get; init; }

    /// <summary>Payload for <c>type == "pois"</c>: quest marks.</summary>
    public List<WebSyncQuest>? Quests { get; init; }

    /// <summary>Payload for <c>type == "meta"</c>: map notes.</summary>
    public List<WebSyncAnnotation>? Annotations { get; init; }

    /// <summary>Payload for <c>type == "meta"</c>: party members.</summary>
    public List<WebSyncPeer>? Peers { get; init; }

    /// <summary>Payload for <c>type == "meta"</c>: recent pings.</summary>
    public List<WebSyncPing>? Pings { get; init; }

    /// <summary>Payload for <c>type == "raid"</c>: whether the player is in a raid.</summary>
    public bool? InRaid { get; init; }
}

/// <summary>
/// Source-generated serialization context for the WebSync wire format.
/// </summary>
/// <remarks>
/// Every nested DTO is registered explicitly. Source generation only follows types it can see from a
/// registered root, and a type reached solely through a collection or a nullable reference gets no
/// top-level metadata of its own; listing them here keeps each one's metadata present and stable.
/// Do not trim this list -- the build may still succeed while a runtime <c>GetTypeInfo</c> call
/// returns null, which fails only the request that needs it.
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WebSyncSnapshot))]
[JsonSerializable(typeof(WebSyncMessage))]
[JsonSerializable(typeof(WebSyncMap))]
[JsonSerializable(typeof(WebSyncImagery))]
[JsonSerializable(typeof(WebSyncProjection))]
[JsonSerializable(typeof(WebSyncRect))]
[JsonSerializable(typeof(WebSyncFloor))]
[JsonSerializable(typeof(WebSyncPlayer))]
[JsonSerializable(typeof(WebSyncPoint))]
[JsonSerializable(typeof(WebSyncExtract))]
[JsonSerializable(typeof(WebSyncBasePoint))]
[JsonSerializable(typeof(WebSyncWaypoint))]
[JsonSerializable(typeof(WebSyncRouteOwner))]
[JsonSerializable(typeof(WebSyncPeerRoute))]
[JsonSerializable(typeof(WebSyncPoi))]
[JsonSerializable(typeof(WebSyncQuest))]
[JsonSerializable(typeof(WebSyncAnnotation))]
[JsonSerializable(typeof(WebSyncPeer))]
[JsonSerializable(typeof(WebSyncPing))]
[JsonSerializable(typeof(WebSyncSettings))]
internal sealed partial class WebSyncJsonContext : JsonSerializerContext;
