using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Diagnostics;
using TarkovMapCompanion.Localization;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Party;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Screenshots;
using TarkovMapCompanion.Settings;
using TarkovMapCompanion.Vision;

namespace TarkovMapCompanion.WebSync;

// ---------------------------------------------------------------------------
// StateBroadcaster and its snapshot builder.
//
// The file is split in two on purpose:
//
//   * SnapshotBuilder is a pure-ish static class. Everything that turns the live
//     overlays into WebSync DTOs lives there, so a unit test can construct the
//     real overlay objects (PlayerOverlay, PoiOverlay, QuestOverlay, ...) plus a
//     GameMap and assert the field mapping without a socket, Kestrel or a wired
//     MapSession. BuildPlayer is factored out because both the full snapshot and
//     the throttled "player" incremental message call it, so the two cannot drift.
//
//   * StateBroadcaster is the stateful half: it subscribes to MapSession, owns the
//     WebSocket client table, throttles the high-frequency FixApplied event and
//     serializes with the source-generated WebSyncJsonContext.
//
// Thread contract (binding, see docs/websync-implementation-spec.md section 4.2):
// every MapSession event fires on a watcher/socket background thread. This class
// runs there and NEVER marshals to the Avalonia UI thread and never touches
// Dispatcher. Overlay reads are lock-protected snapshots except QuestOverlay.Marks,
// which returns the live backing list; that one is copied immediately on read (see
// BuildQuests) so it is never held across an await. No overlay lock is taken here
// and no drawing code is called.
// ---------------------------------------------------------------------------

/// <summary>
/// Which optional sections a snapshot includes, and the one client-facing behaviour
/// flag, mirrored from <see cref="AppSettings"/> at build time.
/// </summary>
/// <remarks>
/// A small value record rather than the whole <see cref="AppSettings"/> so the snapshot
/// builder stays decoupled from settings persistence and a test can state its intent in
/// one line. The content toggles are the spec's "content filter": a false flag produces
/// an empty list for that section, never a missing one.
/// </remarks>
internal readonly record struct WebSyncSections(
    bool ShowPois,
    bool ShowQuests,
    bool ShowAnnotations,
    bool FollowPlayerFloor);

/// <summary>
/// Turns the desktop's live overlays into the WebSync wire DTOs.
/// </summary>
/// <remarks>
/// Stateless by design: every method takes the objects it needs as parameters and returns
/// fresh DTOs. That is what makes the mapping testable without Kestrel, without sockets and
/// without a fully wired <c>MapSession</c> -- a test constructs the real overlays, calls
/// <see cref="Build"/> (or one of the section methods) and asserts the fields.
/// </remarks>
internal static class SnapshotBuilder
{
    /// <summary>
    /// Builds a complete snapshot from the current desktop state.
    /// </summary>
    /// <param name="map">The map being shown, or null when none is selected.</param>
    /// <param name="player">The local player overlay; supplies position, heading and trail.</param>
    /// <param name="extract">The guide-line overlay; supplies the active target.</param>
    /// <param name="pois">The point-of-interest overlay.</param>
    /// <param name="quests">The quest-marker overlay.</param>
    /// <param name="annotations">The map-note overlay, already scoped to the current map.</param>
    /// <param name="waypoints">The route overlay; supplies the player's own pins and teammates' shared routes.</param>
    /// <param name="party">The party session; supplies peers and relayed routes.</param>
    /// <param name="pings">The pings to include, read from the ping overlay.</param>
    /// <param name="sections">The content-filter and behaviour flags.</param>
    /// <param name="inRaid">Whether a raid is currently running.</param>
    /// <param name="generatedAt">When the snapshot was built, UTC.</param>
    internal static WebSyncSnapshot Build(
        GameMap? map,
        PlayerOverlay player,
        ExtractLineOverlay extract,
        PoiOverlay pois,
        QuestOverlay quests,
        AnnotationOverlay annotations,
        WaypointOverlay waypoints,
        PartySession party,
        IReadOnlyList<MapPing> pings,
        WebSyncSections sections,
        bool inRaid,
        DateTime generatedAt)
    {
        return new WebSyncSnapshot
        {
            GeneratedAt = generatedAt,
            InRaid = inRaid,
            Map = BuildMap(map, player.ActiveFloors),
            Player = BuildPlayer(map, player),
            Extract = BuildExtract(extract),
            Waypoints = BuildWaypoints(waypoints),
            SharedRoutes = BuildSharedRoutes(waypoints.SharedRoutes),
            PeerRoutes = BuildPeerRoutes(party),
            Pois = sections.ShowPois ? BuildPois(pois) : new List<WebSyncPoi>(),
            Quests = sections.ShowQuests ? BuildQuests(quests) : new List<WebSyncQuest>(),
            Annotations = sections.ShowAnnotations ? BuildAnnotations(annotations) : new List<WebSyncAnnotation>(),
            Peers = BuildPeers(party),
            Pings = BuildPings(pings),
            Settings = new WebSyncSettings { FollowPlayerFloor = sections.FollowPlayerFloor },
        };
    }

    /// <summary>Builds the map descriptor: imagery, projection, rects and floors.</summary>
    internal static WebSyncMap? BuildMap(GameMap? map, IReadOnlyCollection<string>? activeFloors)
    {
        if (map is null)
            return null;

        return new WebSyncMap
        {
            Key = map.Key,
            DisplayName = map.DisplayName,
            Imagery = BuildImagery(map),
            Projection = BuildProjection(map),
            BaseRect = ToRect(map.BaseRect),
            SvgBaseRect = ToRect(map.SvgBaseRect),

            // Display Name, never SvgLayerId: the two are different namespaces and the
            // client switches floors by the label it was shown.
            Floors = map.Floors.Select(f => new WebSyncFloor { Name = f.Name }).ToList(),
            ActiveFloors = activeFloors?.ToList() ?? new List<string>(),
        };
    }

    /// <summary>
    /// Builds the local player, or null before the first fix.
    /// </summary>
    /// <remarks>
    /// Shared by the full snapshot and the throttled <c>player</c> message, so the two can
    /// never report a different position, floor or trail for the same moment.
    /// </remarks>
    internal static WebSyncPlayer? BuildPlayer(GameMap? map, PlayerOverlay player)
    {
        if (player.Current is not { } fix)
            return null;

        // The floor is the first one that claims this height; no floor claiming it means the
        // base level, which the wire format reports as null.
        var floor = map?.Floors.FirstOrDefault(f => f.Covers(fix.Position))?.Name;

        // ScreenAngleDegrees folds in the map's coordinate rotation plus the 180-degree flip;
        // re-deriving it client-side would reverse the marker on rotated maps, so it is
        // precomputed here. Without a map there is nothing to fold in, so the raw yaw stands.
        var angle = map is { } m
            ? m.Projection.ScreenAngleDegrees(fix.YawDegrees)
            : MapProjection.Normalize360(fix.YawDegrees);

        return new WebSyncPlayer
        {
            X = fix.Position.X,
            Z = fix.Position.Z,
            YawDegrees = fix.YawDegrees,
            ScreenAngleDegrees = angle,
            Floor = floor,

            // History is already a copy handed out under the overlay's lock.
            Trail = player.History.Select(p => new WebSyncPoint { X = p.Position.X, Z = p.Position.Z }).ToList(),
        };
    }

    /// <summary>
    /// Builds the active guide line, or null when there is no target or waypoint guide.
    /// </summary>
    /// <remarks>
    /// The only base-space value on the wire is <see cref="WebSyncExtract.GuideBase"/>: the
    /// guide-line maths is handed over ready to draw rather than re-derived in the browser.
    /// </remarks>
    internal static WebSyncExtract? BuildExtract(ExtractLineOverlay extract)
    {
        if (extract.GuideBase is not { } guide)
            return null;

        var target = extract.Target;

        return new WebSyncExtract
        {
            TargetId = target?.Id,
            TargetName = target is null ? null : Localizer.Pick(target.Name, target.NameZh),
            GuideBase = new WebSyncBasePoint { X = guide.X, Y = guide.Y },
            DistanceMeters = extract.DistanceMeters,
            RelativeBearingDegrees = extract.RelativeBearingDegrees,
        };
    }

    /// <summary>Builds the player's own route pins, in order, in game coordinates.</summary>
    internal static List<WebSyncWaypoint> BuildWaypoints(WaypointOverlay waypoints)
    {
        // Waypoints hands out a copy, so enumerating it off the watcher thread is safe.
        var source = waypoints.Waypoints;

        return source
            .Select(w => new WebSyncWaypoint
            {
                N = w.Number,
                X = w.Position.X,
                Z = w.Position.Z,
                Visited = w.Visited,
            })
            .ToList();
    }

    /// <summary>
    /// Builds teammates' colored routes from the overlay's shared-route list.
    /// </summary>
    /// <remarks>
    /// Read straight from <see cref="WaypointOverlay.SharedRoutes"/>, which
    /// <c>MapSession.RebuildSharedRoutes</c> populates from <c>PartySession.Routes</c> with the
    /// local player's own route already dropped. Reading the overlay instead of re-deriving the
    /// list from the party session is what stops the two from drifting apart. Self-exclusion is
    /// therefore MapSession's job, applied once upstream, and deliberately not repeated here.
    /// </remarks>
    internal static List<WebSyncRouteOwner> BuildSharedRoutes(IReadOnlyList<WaypointOverlay.SharedRoute> routes)
    {
        return routes
            .Select(route => new WebSyncRouteOwner
            {
                Owner = route.Owner,
                Color = ColorCodec.ToHex(route.Color),
                Points = ToPoints(route.Points),
            })
            .ToList();
    }

    /// <summary>
    /// Builds teammates' routes as relayed by the party session, with no color.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="BuildSharedRoutes"/> on purpose: the wire type deliberately
    /// carries no color here and the browser colors it by matching the peer's name.
    /// </remarks>
    internal static List<WebSyncPeerRoute> BuildPeerRoutes(PartySession party)
    {
        var mine = party.SelfName;
        var result = new List<WebSyncPeerRoute>();

        foreach (var route in party.Routes)
        {
            if (string.Equals(route.Name, mine, StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(new WebSyncPeerRoute
            {
                Name = route.Name,
                Points = ToPoints(route.Points),
            });
        }

        return result;
    }

    /// <summary>Builds every point of interest on the current map, already localized.</summary>
    internal static List<WebSyncPoi> BuildPois(PoiOverlay pois)
    {
        // The overlay replaces its list wholesale rather than mutating it, so holding the
        // reference for the duration of this loop is safe and needs no copy.
        var source = pois.Pois;
        var result = new List<WebSyncPoi>(source.Count);

        foreach (var poi in source)
        {
            result.Add(new WebSyncPoi
            {
                Id = poi.Id,
                Kind = poi.Kind.ToString(),

                // Localization happens at this call site, never in the DTO layer.
                Name = Localizer.Pick(poi.Name, poi.NameZh),
                X = poi.Position.X,
                Z = poi.Position.Z,
                Available = poi.AvailableThisRaid,
                Conditional = poi.IsConditional,
                SingleUse = poi.IsSingleUse,
                Elevation = poi.Elevation is { } band
                    ? new List<double> { band.Bottom, band.Top }
                    : null,
            });
        }

        return result;
    }

    /// <summary>Builds every tracked quest objective mark on the current map.</summary>
    internal static List<WebSyncQuest> BuildQuests(QuestOverlay quests)
    {
        // QuestOverlay.Marks is the one overlay that returns its live backing list under its
        // lock instead of a copy. Copy it immediately so it is never enumerated while the UI
        // thread is rebuilding it -- the same race PlayerOverlayConcurrencyTests guards.
        var marks = quests.Marks.ToArray();

        return marks
            .Select(m => new WebSyncQuest
            {
                Label = m.Label,
                X = m.Position.X,
                Z = m.Position.Z,
            })
            .ToList();
    }

    /// <summary>Builds the map notes for the current map.</summary>
    internal static List<WebSyncAnnotation> BuildAnnotations(AnnotationOverlay annotations)
    {
        // The overlay holds exactly the current map's notes (MapSession.RefreshAnnotations sets
        // it from AnnotationStore.ForMap). Replaced wholesale, so the reference is safe to walk.
        var source = annotations.Annotations;

        return source
            .Select(a => new WebSyncAnnotation
            {
                Id = a.Id,
                X = a.X,
                Z = a.Z,
                Text = a.Text,
                Author = a.Author,
            })
            .ToList();
    }

    /// <summary>
    /// Builds every placed party member. The local player is excluded: they already ship as
    /// <c>player</c>, and drawing them twice would put two markers on the same spot.
    /// </summary>
    internal static List<WebSyncPeer> BuildPeers(PartySession party)
    {
        var result = new List<WebSyncPeer>();

        foreach (var peer in party.Peers)
        {
            if (!peer.HasPosition || peer.IsSelf)
                continue;

            result.Add(new WebSyncPeer
            {
                Name = peer.Name,
                X = peer.Position.X,
                Z = peer.Position.Z,
                YawDegrees = peer.Yaw,

                // PartyPeer.Color is already a #RRGGBB string; canonicalize it through the same
                // codec the rest of the app uses, and pass null when they never picked one.
                Color = CanonicalColor(peer.Color),
            });
        }

        return result;
    }

    /// <summary>Builds the recent squad pings, in game coordinates.</summary>
    internal static List<WebSyncPing> BuildPings(IReadOnlyList<MapPing> pings)
    {
        return pings
            .Select(p => new WebSyncPing
            {
                X = p.Position.X,
                Z = p.Position.Z,
                At = p.PlacedUtc,
            })
            .ToList();
    }

    private static WebSyncImagery? BuildImagery(GameMap map)
    {
        if (map.HasSvg)
            return new WebSyncImagery { Type = "svg", Url = $"/map/{map.Key}.svg" };

        if (map.HasTiles)
        {
            return new WebSyncImagery
            {
                Type = "tiles",
                UrlTemplate = $"/tiles/{map.Key}/{{z}}/{{x}}/{{y}}.png",
                TileSize = map.TileSize,
                MinZoom = map.MinZoomLevel,
                MaxZoom = map.MaxZoomLevel,
            };
        }

        return null;
    }

    private static WebSyncProjection BuildProjection(GameMap map)
    {
        // Straight from the projection's own read-only affine; the coefficients are never
        // recomputed here, so they cannot drift from ToBase.
        var affine = map.Projection.Affine;

        return new WebSyncProjection
        {
            Cos = affine.Cos,
            Sin = affine.Sin,
            A = affine.A,
            B = affine.B,
            C = affine.C,
            D = affine.D,
            CoordinateRotationDegrees = affine.CoordinateRotationDegrees,
        };
    }

    private static WebSyncRect ToRect(MapRect rect) =>
        new() { X = rect.Left, Y = rect.Top, W = rect.Width, H = rect.Height };

    private static List<WebSyncPoint> ToPoints(IReadOnlyList<RoutePoint> points) =>
        points.Select(p => new WebSyncPoint { X = p.X, Z = p.Z }).ToList();

    private static List<WebSyncPoint> ToPoints(IReadOnlyList<GamePosition> points) =>
        points.Select(p => new WebSyncPoint { X = p.X, Z = p.Z }).ToList();

    private static string? CanonicalColor(string? text) =>
        ColorCodec.TryParse(text, out var color) ? ColorCodec.ToHex(color) : null;
}

/// <summary>
/// Broadcasts the read-only desktop state to WebSocket clients, and serves the current
/// snapshot to the HTTP endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Subscribes to the <see cref="MapSession"/> events (and the party roster/route events), throttles
/// the fix stream to 2 Hz, and fans out serialized messages to a thread-safe client table. It runs
/// entirely on the event-raising background threads and never marshals to the UI thread.
/// </para>
/// <para>
/// Disposal is a hard requirement: <see cref="MapSession.Dispose"/> detaches only its own internal
/// handlers, so this class remembers every subscription and removes each one, mirroring
/// <c>MinimapWindow.Teardown()</c>. Ending a broadcast is the only way a repeatedly opened
/// Preferences or Minimap window can avoid leaking subscriptions and retaining dead sockets.
/// </para>
/// </remarks>
internal sealed class StateBroadcaster : IDisposable
{
    /// <summary>Fixes are coalesced to this window: at most one player message per 500 ms.</summary>
    private static readonly TimeSpan FixThrottle = TimeSpan.FromMilliseconds(500);

    /// <summary>A send that has not completed within this long gets its client dropped.</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);

    private readonly MapSession _session;
    private readonly AppSettings _settings;

    /// <summary>The live WebSocket clients, keyed by socket so unregistration is a lookup.</summary>
    private readonly ConcurrentDictionary<WebSocket, Client> _clients = new();

    /// <summary>
    /// Coalesces the fix stream. One one-shot timer, re-armed on demand, plus the newest fix.
    /// </summary>
    /// <remarks>
    /// Chosen over a fire-and-forget <c>Task.Delay</c> loop because at most one timer is ever
    /// pending, so it cannot pile up or spin; and because the newest fix is always stored, the
    /// final fix of a burst is never lost -- a fix arriving after the timer has fired simply arms
    /// the next one.
    /// </remarks>
    private readonly object _fixGate = new();
    private readonly Timer _fixTimer;
    private WebSyncPlayer? _pendingPlayer;
    private bool _fixTimerArmed;

    /// <summary>Whether a raid is running. The only source is the event, so it is cached here.</summary>
    private volatile bool _inRaid;

    private volatile bool _disposed;

    /// <summary>
    /// Creates a broadcaster over a session and starts listening.
    /// </summary>
    /// <param name="session">The session whose drawings and events are mirrored.</param>
    /// <param name="settings">The current settings, read fresh on every snapshot build.</param>
    internal StateBroadcaster(MapSession session, AppSettings settings)
    {
        _session = session;
        _settings = settings;

        _fixTimer = new Timer(FlushFix, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        // Method groups, not lambdas: each one can be removed again in Dispose. This is exactly
        // the discipline MinimapWindow.Teardown() uses.
        _session.FixApplied += OnFixApplied;
        _session.MapChanged += OnMapChanged;
        _session.WaypointsChanged += OnWaypointsChanged;
        _session.SelectionChanged += OnSelectionChanged;
        _session.PoisChanged += OnPoisChanged;
        _session.ExitAvailabilityChanged += OnExitAvailabilityChanged;
        _session.AnnotationsChanged += OnAnnotationsChanged;
        _session.QuestsChanged += OnQuestsChanged;
        _session.RaidStateChanged += OnRaidStateChanged;
        _session.PingAdded += OnPingAdded;

        // Not in the spec's ten events, but the "meta" and "route" incremental messages carry
        // peers and relayed routes, and nothing on MapSession raises for either. Subscribing
        // directly is what keeps a teammate's marker and route current without a screenshot.
        _session.Party.Changed += OnPartyChanged;
        _session.Party.RoutesChanged += OnPartyRoutesChanged;
    }

    /// <summary>
    /// Builds the current full snapshot.
    /// </summary>
    /// <remarks>
    /// Callable on any thread, and touches no Kestrel state; this is what serves
    /// <c>GET /api/state</c> and what the first frame of a new WebSocket is built from.
    /// </remarks>
    public WebSyncSnapshot BuildSnapshot()
    {
        var sections = new WebSyncSections(
            _settings.WebSyncShowPois,
            _settings.WebSyncShowQuests,
            _settings.WebSyncShowAnnotations,
            _settings.WebSyncFollowPlayerFloor);

        return SnapshotBuilder.Build(
            _session.CurrentMap,
            _session.Player,
            _session.ExtractLine,
            _session.Pois,
            _session.Quests,
            _session.Annotations,
            _session.Waypoints,
            _session.Party,
            _session.Pings.Snapshot(),
            sections,
            _inRaid,
            DateTime.UtcNow);
    }

    /// <summary>
    /// Adds a socket to the client table and immediately sends it the full snapshot.
    /// </summary>
    /// <remarks>
    /// Safe to call from a Kestrel accept thread. The client's send gate is taken before the
    /// client is published into the table, so the first frame physically cannot be overtaken by
    /// an incremental message that a concurrent event is already broadcasting.
    /// </remarks>
    public void Register(WebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);

        if (_disposed)
        {
            TryDispose(socket);
            return;
        }

        var client = new Client(socket);

        client.Gate.Wait();

        if (!_clients.TryAdd(socket, client))
        {
            // Already registered; hand the gate back and drop the duplicate wrapper.
            client.Gate.Release();
            client.Dispose();
            return;
        }

        _ = SendFirstFrameAsync(client);
    }

    /// <summary>
    /// Removes a socket from the client table and disposes it. Idempotent.
    /// </summary>
    public void Unregister(WebSocket socket)
    {
        if (socket is null)
            return;

        if (_clients.TryRemove(socket, out var client))
            client.Dispose();
    }

    /// <summary>
    /// Unsubscribes every handler, closes every socket, and becomes inert.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _session.FixApplied -= OnFixApplied;
        _session.MapChanged -= OnMapChanged;
        _session.WaypointsChanged -= OnWaypointsChanged;
        _session.SelectionChanged -= OnSelectionChanged;
        _session.PoisChanged -= OnPoisChanged;
        _session.ExitAvailabilityChanged -= OnExitAvailabilityChanged;
        _session.AnnotationsChanged -= OnAnnotationsChanged;
        _session.QuestsChanged -= OnQuestsChanged;
        _session.RaidStateChanged -= OnRaidStateChanged;
        _session.PingAdded -= OnPingAdded;
        _session.Party.Changed -= OnPartyChanged;
        _session.Party.RoutesChanged -= OnPartyRoutesChanged;

        _fixTimer.Dispose();

        foreach (var client in _clients.Values)
            Unregister(client.Socket);

        _clients.Clear();
    }

    // ---- Event handlers -----------------------------------------------------

    private void OnFixApplied(object? sender, PlayerFix fix)
    {
        if (_disposed)
            return;

        // Built through the same method the snapshot uses, so the two cannot diverge.
        if (SnapshotBuilder.BuildPlayer(_session.CurrentMap, _session.Player) is not { } player)
            return;

        lock (_fixGate)
        {
            _pendingPlayer = player;

            if (_fixTimerArmed)
                return;

            _fixTimerArmed = true;
            _fixTimer.Change(FixThrottle, Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushFix(object? state)
    {
        if (_disposed)
            return;

        WebSyncPlayer? player;

        lock (_fixGate)
        {
            _fixTimerArmed = false;
            player = _pendingPlayer;
            _pendingPlayer = null;
        }

        if (player is not null)
            Broadcast(new WebSyncMessage { Type = "player", Player = player });
    }

    private void OnMapChanged(object? sender, GameMap map) => BroadcastFullSnapshot();

    private void OnWaypointsChanged(object? sender, EventArgs e) => BroadcastRoute();

    private void OnSelectionChanged(object? sender, MapPoi? poi) => BroadcastRoute();

    private void OnPoisChanged(object? sender, EventArgs e) => BroadcastPois();

    private void OnExitAvailabilityChanged(object? sender, ExitAvailability? availability) => BroadcastPois();

    private void OnAnnotationsChanged(object? sender, EventArgs e) => BroadcastMeta();

    private void OnQuestsChanged(object? sender, EventArgs e) => BroadcastPois();

    private void OnRaidStateChanged(object? sender, bool inRaid)
    {
        _inRaid = inRaid;

        if (_disposed)
            return;

        Broadcast(new WebSyncMessage { Type = "raid", InRaid = inRaid });
    }

    // Pings are read live from PingOverlay when a message is built, never cached. This
    // subscription is kept purely as a prompt trigger: without it a new ping would wait for the
    // next unrelated meta broadcast rather than reaching clients as soon as it lands.
    private void OnPingAdded(object? sender, MapPing ping) => BroadcastMeta();

    private void OnPartyChanged(object? sender, EventArgs e) => BroadcastMeta();

    private void OnPartyRoutesChanged(object? sender, EventArgs e) => BroadcastRoute();

    // ---- Message builders ---------------------------------------------------

    private void BroadcastFullSnapshot()
    {
        if (_disposed)
            return;

        Broadcast(new WebSyncMessage { Type = "map", Snapshot = BuildSnapshot() });
    }

    private void BroadcastRoute()
    {
        if (_disposed)
            return;

        Broadcast(new WebSyncMessage
        {
            Type = "route",
            Waypoints = SnapshotBuilder.BuildWaypoints(_session.Waypoints),
            SharedRoutes = SnapshotBuilder.BuildSharedRoutes(_session.Waypoints.SharedRoutes),
            PeerRoutes = SnapshotBuilder.BuildPeerRoutes(_session.Party),
            Extract = SnapshotBuilder.BuildExtract(_session.ExtractLine),
        });
    }

    private void BroadcastPois()
    {
        if (_disposed)
            return;

        Broadcast(new WebSyncMessage
        {
            Type = "pois",
            Pois = _settings.WebSyncShowPois
                ? SnapshotBuilder.BuildPois(_session.Pois)
                : new List<WebSyncPoi>(),
            Quests = _settings.WebSyncShowQuests
                ? SnapshotBuilder.BuildQuests(_session.Quests)
                : new List<WebSyncQuest>(),
        });
    }

    private void BroadcastMeta()
    {
        if (_disposed)
            return;

        Broadcast(new WebSyncMessage
        {
            Type = "meta",
            Annotations = _settings.WebSyncShowAnnotations
                ? SnapshotBuilder.BuildAnnotations(_session.Annotations)
                : new List<WebSyncAnnotation>(),
            Peers = SnapshotBuilder.BuildPeers(_session.Party),
            Pings = SnapshotBuilder.BuildPings(_session.Pings.Snapshot()),
        });
    }

    // ---- Client table and sends ---------------------------------------------

    private void Broadcast(WebSyncMessage message)
    {
        if (_disposed)
            return;

        string json;

        try
        {
            json = JsonSerializer.Serialize(message, WebSyncJsonContext.Default.WebSyncMessage);
        }
        catch (Exception ex)
        {
            // Serialization is local and should not fail, but a throw here would escape onto a
            // watcher thread and take the process with it, so it is contained.
            Log.Error("websync failed to serialize a message", ex);
            return;
        }

        // Fire-and-forget per client: one slow socket must never block the rest, and each send
        // is serialized through that client's own gate because two events can fire at once.
        foreach (var client in _clients.Values)
            _ = SendGuardedAsync(client, json);
    }

    private async Task SendFirstFrameAsync(Client client)
    {
        try
        {
            var message = new WebSyncMessage { Type = "map", Snapshot = BuildSnapshot() };
            var json = JsonSerializer.Serialize(message, WebSyncJsonContext.Default.WebSyncMessage);

            // The gate is already held from Register; this keeps the snapshot ahead of any
            // incremental message waiting on the same gate.
            await SendCoreAsync(client, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"websync failed to send the first frame: {ex.Message}");
            Unregister(client.Socket);
        }
        finally
        {
            try { client.Gate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task SendGuardedAsync(Client client, string json)
    {
        var gate = client.Gate;

        try
        {
            await gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The client was unregistered while this send was queued.
            return;
        }

        try
        {
            await SendCoreAsync(client, json).ConfigureAwait(false);
        }
        finally
        {
            try { gate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task SendCoreAsync(Client client, string json)
    {
        var socket = client.Socket;

        if (socket.State != WebSocketState.Open)
        {
            Unregister(socket);
            return;
        }

        // A bounded send: a stalled client is dropped rather than allowed to hold a slot forever.
        using var timeout = new CancellationTokenSource(SendTimeout);

        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket
                .SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException
                                      or WebSocketException
                                      or ObjectDisposedException
                                      or InvalidOperationException)
        {
            // Slow or broken. Drop it; clients reconnect and resynchronize with a full snapshot.
            Log.Warn($"websync dropping a client: {ex.GetType().Name}: {ex.Message}");
            Unregister(socket);
        }
    }

    private static void TryDispose(WebSocket socket)
    {
        try { socket.Dispose(); } catch (Exception) { }
    }

    /// <summary>One connected client: its socket and the gate that serializes writes to it.</summary>
    private sealed class Client : IDisposable
    {
        /// <summary>Wraps a freshly accepted socket.</summary>
        internal Client(WebSocket socket) => Socket = socket;

        /// <summary>The WebSocket this client is reached through.</summary>
        internal WebSocket Socket { get; }

        /// <summary>One writer at a time, so two concurrent broadcasts cannot interleave frames.</summary>
        internal SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Closes the socket and releases the send gate.</summary>
        public void Dispose()
        {
            TryDispose(Socket);
            Gate.Dispose();
        }
    }
}
