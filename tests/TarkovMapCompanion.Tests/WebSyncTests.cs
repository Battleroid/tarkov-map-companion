using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Party;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Screenshots;
using TarkovMapCompanion.Settings;
using TarkovMapCompanion.WebSync;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Pins the WebSync wire format: the JSON shape the browser client reads back, the LAN address
/// preference rule, and the mapping from the live overlays into the DTOs.
/// </summary>
/// <remarks>
/// The snapshot builder is deliberately pure, so everything here uses the real overlay objects and
/// the real embedded catalog -- no sockets, no Kestrel and no network. <c>LanAddress.Best()</c> is
/// not called because it reads this machine's adapters; the testable <c>Select</c> rule is.
/// </remarks>
public sealed class WebSyncTests
{
    private static readonly MapCatalog Catalog = MapCatalog.LoadEmbedded();

    private static PlayerFix Fix(double x, double z, double yaw = 0) => new()
    {
        Position = new GamePosition(x, 0, z),
        YawDegrees = yaw,
        Rotation = (0, 0, 0, 1),
        RaidTimeHours = 10,
        TakenAt = new DateTime(2026, 8, 7, 19, 31, 0, DateTimeKind.Utc),
        FilePath = $@"C:\s\{x}-{z}.png",
    };

    /// <summary>A snapshot with one element in every list, for the serialization checks.</summary>
    private static WebSyncSnapshot PopulatedSnapshot()
    {
        var generatedAt = new DateTime(2026, 8, 7, 19, 31, 0, DateTimeKind.Utc);

        return new WebSyncSnapshot
        {
            GeneratedAt = generatedAt,
            InRaid = true,
            Map = new WebSyncMap
            {
                Key = "customs",
                DisplayName = "Customs",
                Imagery = new WebSyncImagery { Type = "svg", Url = "/map/customs.svg" },
                Projection = new WebSyncProjection
                {
                    Cos = -1,
                    Sin = 0,
                    A = 0.239,
                    B = 168.65,
                    C = 0.239,
                    D = 136.35,
                    CoordinateRotationDegrees = 180,
                },
                BaseRect = new WebSyncRect { X = 1, Y = 2, W = 3, H = 4 },
                SvgBaseRect = new WebSyncRect { X = 1, Y = 2, W = 3, H = 4 },
                Floors = [new WebSyncFloor { Name = "2nd Floor" }],
                ActiveFloors = ["2nd Floor"],
            },
            Player = new WebSyncPlayer
            {
                X = 10,
                Z = 20,
                YawDegrees = 30,
                ScreenAngleDegrees = 210,
                Floor = "2nd Floor",
                Trail = [new WebSyncPoint { X = 10, Z = 20 }],
            },
            Extract = new WebSyncExtract
            {
                TargetId = "ex_0",
                TargetName = "Crossroads",
                GuideBase = new WebSyncBasePoint { X = 5, Y = 6 },
                DistanceMeters = 42,
                RelativeBearingDegrees = -12,
            },
            Waypoints = [new WebSyncWaypoint { N = 1, X = 100, Z = 200, Visited = false }],
            SharedRoutes =
            [
                new WebSyncRouteOwner
                {
                    Owner = "Timo",
                    Color = "#F5C942",
                    Points = [new WebSyncPoint { X = 1, Z = 2 }],
                },
            ],
            PeerRoutes =
            [
                new WebSyncPeerRoute { Name = "Timo", Points = [new WebSyncPoint { X = 3, Z = 4 }] },
            ],
            Pois =
            [
                new WebSyncPoi
                {
                    Id = "p1",
                    Kind = "ExtractPmc",
                    Name = "Crossroads",
                    X = 7,
                    Z = 8,
                    Available = true,
                    Conditional = false,
                    SingleUse = false,
                    Elevation = [0, 4],
                },
            ],
            Quests = [new WebSyncQuest { Label = "Task: objective", X = 9, Z = 10 }],
            Annotations = [new WebSyncAnnotation { Id = "a1", X = 11, Z = 12, Text = "Watch out", Author = "Timo" }],
            Peers = [new WebSyncPeer { Name = "Timo", X = 13, Z = 14, YawDegrees = 90, Color = "#F5C942" }],
            Pings = [new WebSyncPing { X = 15, Z = 16, At = generatedAt }],
            Settings = new WebSyncSettings { FollowPlayerFloor = true },
        };
    }

    // ---- 1. DTO serialization round trip ------------------------------------

    [Fact]
    public void SnapshotJsonIsCamelCaseAndCarriesNoAffineTranslation()
    {
        var json = JsonSerializer.Serialize(PopulatedSnapshot(), WebSyncJsonContext.Default.WebSyncSnapshot);

        Assert.Contains("\"generatedAt\"", json);
        Assert.Contains("\"screenAngleDegrees\"", json);
        Assert.Contains("\"coordinateRotationDegrees\"", json);
        Assert.DoesNotContain("\"GeneratedAt\"", json);
        Assert.DoesNotContain("\"tx\"", json);

        using var document = JsonDocument.Parse(json);
        var projection = document.RootElement.GetProperty("map").GetProperty("projection");

        foreach (var name in new[] { "cos", "sin", "a", "b", "c", "d", "coordinateRotationDegrees" })
            Assert.True(projection.TryGetProperty(name, out _), name);

        // B and D already are the translation terms; a tx/ty would be a second, conflicting pair.
        Assert.False(projection.TryGetProperty("tx", out _));
        Assert.False(projection.TryGetProperty("ty", out _));
    }

    [Fact]
    public void SnapshotRoundTripsThroughTheSourceGeneratedContext()
    {
        var snapshot = PopulatedSnapshot();

        var json = JsonSerializer.Serialize(snapshot, WebSyncJsonContext.Default.WebSyncSnapshot);
        var back = JsonSerializer.Deserialize(json, WebSyncJsonContext.Default.WebSyncSnapshot)!;

        Assert.Equal(snapshot.Version, back.Version);
        Assert.Equal(snapshot.GeneratedAt, back.GeneratedAt);
        Assert.Equal(snapshot.InRaid, back.InRaid);

        // Map, imagery and projection carry no lists, so the records compare structurally.
        Assert.Equal(snapshot.Map!.Key, back.Map!.Key);
        Assert.Equal(snapshot.Map.DisplayName, back.Map.DisplayName);
        Assert.Equal(snapshot.Map.Imagery, back.Map.Imagery);
        Assert.Equal(snapshot.Map.Projection, back.Map.Projection);
        Assert.Equal(snapshot.Map.BaseRect, back.Map.BaseRect);
        Assert.Equal(snapshot.Map.SvgBaseRect, back.Map.SvgBaseRect);

        // Player and extract: scalars directly, then the one list element-wise, because records
        // holding a List<T> compare those lists by reference rather than by content.
        Assert.Equal(snapshot.Player!.X, back.Player!.X);
        Assert.Equal(snapshot.Player.Z, back.Player.Z);
        Assert.Equal(snapshot.Player.YawDegrees, back.Player.YawDegrees);
        Assert.Equal(snapshot.Player.ScreenAngleDegrees, back.Player.ScreenAngleDegrees);
        Assert.Equal(snapshot.Player.Floor, back.Player.Floor);

        var trail = Assert.Single(back.Player.Trail);
        Assert.Equal(10.0, trail.X);
        Assert.Equal(20.0, trail.Z);

        Assert.Equal(snapshot.Extract!.TargetId, back.Extract!.TargetId);
        Assert.Equal(snapshot.Extract.TargetName, back.Extract.TargetName);
        Assert.Equal(snapshot.Extract.GuideBase, back.Extract.GuideBase);
        Assert.Equal(snapshot.Extract.DistanceMeters, back.Extract.DistanceMeters);
        Assert.Equal(snapshot.Extract.RelativeBearingDegrees, back.Extract.RelativeBearingDegrees);

        // Every list: count, then the element fields, for the same reference-equality reason.
        var waypoint = Assert.Single(back.Waypoints);
        Assert.Equal(1, waypoint.N);
        Assert.Equal(100.0, waypoint.X);
        Assert.Equal(200.0, waypoint.Z);
        Assert.False(waypoint.Visited);

        var shared = Assert.Single(back.SharedRoutes);
        Assert.Equal("Timo", shared.Owner);
        Assert.Equal("#F5C942", shared.Color);
        Assert.Equal(1.0, Assert.Single(shared.Points).X);

        var peerRoute = Assert.Single(back.PeerRoutes);
        Assert.Equal("Timo", peerRoute.Name);
        Assert.Equal(3.0, Assert.Single(peerRoute.Points).X);

        var poi = Assert.Single(back.Pois);
        Assert.Equal("p1", poi.Id);
        Assert.Equal("ExtractPmc", poi.Kind);
        Assert.Equal("Crossroads", poi.Name);
        Assert.Equal(7.0, poi.X);
        Assert.Equal(8.0, poi.Z);
        Assert.True(poi.Available);
        Assert.Equal(new List<double> { 0, 4 }, poi.Elevation);

        var quest = Assert.Single(back.Quests);
        Assert.Equal("Task: objective", quest.Label);
        Assert.Equal(9.0, quest.X);

        var annotation = Assert.Single(back.Annotations);
        Assert.Equal("a1", annotation.Id);
        Assert.Equal("Watch out", annotation.Text);
        Assert.Equal("Timo", annotation.Author);
        Assert.Equal(11.0, annotation.X);

        var peer = Assert.Single(back.Peers);
        Assert.Equal("Timo", peer.Name);
        Assert.Equal(90.0, peer.YawDegrees);
        Assert.Equal("#F5C942", peer.Color);

        var ping = Assert.Single(back.Pings);
        Assert.Equal(15.0, ping.X);
        Assert.Equal(snapshot.GeneratedAt, ping.At);

        Assert.True(back.Settings!.FollowPlayerFloor);
    }

    // ---- 2. LanAddress.Select ----------------------------------------------

    [Fact]
    public void TenRangeWinsOverTheOtherPrivateRanges()
    {
        Assert.Equal(
            IPAddress.Parse("10.0.0.7"),
            LanAddress.Select([IPAddress.Parse("192.168.1.5"), IPAddress.Parse("10.0.0.7")]));

        Assert.Equal(
            IPAddress.Parse("172.16.0.1"),
            LanAddress.Select([IPAddress.Parse("192.168.1.5"), IPAddress.Parse("172.16.0.1")]));

        Assert.Equal(
            IPAddress.Parse("10.0.0.7"),
            LanAddress.Select([IPAddress.Parse("172.20.0.1"), IPAddress.Parse("10.0.0.7")]));
    }

    [Fact]
    public void LoopbackLinkLocalAndIpv6AreNeverSelected()
    {
        Assert.Null(LanAddress.Select([IPAddress.Loopback]));
        Assert.Null(LanAddress.Select([IPAddress.Parse("169.254.10.20")]));
        Assert.Null(LanAddress.Select([IPAddress.IPv6Loopback]));

        // A reachable address among the unusable ones is still chosen.
        Assert.Equal(
            IPAddress.Parse("192.168.1.5"),
            LanAddress.Select(
            [
                IPAddress.Loopback,
                IPAddress.Parse("169.254.10.20"),
                IPAddress.IPv6Loopback,
                IPAddress.Parse("192.168.1.5"),
            ]));
    }

    [Fact]
    public void NoCandidatesGivesNull()
    {
        Assert.Null(LanAddress.Select(null));
        Assert.Null(LanAddress.Select(Array.Empty<IPAddress>()));
    }

    [Fact]
    public void TheLowestAddressInTheWinningRangeWinsRegardlessOfInputOrder()
    {
        IPAddress[] shuffled =
        [
            IPAddress.Parse("10.0.4.1"),
            IPAddress.Parse("10.0.0.9"),
            IPAddress.Parse("10.0.0.3"),
            IPAddress.Parse("10.0.1.200"),
        ];

        var expected = IPAddress.Parse("10.0.0.3");
        Assert.Equal(expected, LanAddress.Select(shuffled));

        // The choice is by bytes, not by enumeration order, so it survives reordering.
        var reversed = shuffled.Reverse().ToArray();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(expected, LanAddress.Select(shuffled));
            Assert.Equal(expected, LanAddress.Select(reversed));
        }
    }

    // ---- 3. SnapshotBuilder field mapping -----------------------------------

    [Fact]
    public void SvgMapsAdvertiseAnSvgUrlUnderTheMapKey()
    {
        var map = Catalog.Find("customs")!;
        Assert.True(map.HasSvg);

        var imagery = SnapshotBuilder.BuildMap(map, null)!.Imagery!;

        Assert.Equal("svg", imagery.Type);
        Assert.Equal("/map/customs.svg", imagery.Url);
        Assert.Null(imagery.UrlTemplate);
    }

    [Fact]
    public void TileOnlyMapsAdvertiseTheirTemplateAndZoomRange()
    {
        var map = Catalog.Find("icebreaker")!;
        Assert.False(map.HasSvg);
        Assert.True(map.HasTiles);

        var imagery = SnapshotBuilder.BuildMap(map, null)!.Imagery!;

        Assert.Equal("tiles", imagery.Type);
        Assert.Equal("/tiles/icebreaker/{z}/{x}/{y}.png", imagery.UrlTemplate);
        Assert.Equal(256, imagery.TileSize!.Value);
        Assert.Equal(1, imagery.MinZoom!.Value);
        Assert.Equal(5, imagery.MaxZoom!.Value);
        Assert.Null(imagery.Url);
    }

    [Fact]
    public void FloorsUseTheMapDisplayNameNotTheSvgLayerId()
    {
        var map = Catalog.Find("customs")!;
        var built = SnapshotBuilder.BuildMap(map, null)!;

        Assert.Equal(map.Floors.Select(f => f.Name), built.Floors.Select(f => f.Name));

        // The two namespaces genuinely differ on Customs, so this is not a vacuous check.
        Assert.Contains("Second_Floor", map.Floors.Select(f => f.SvgLayerId));
        Assert.DoesNotContain("Second_Floor", built.Floors.Select(f => f.Name));
    }

    [Fact]
    public void SectionCoordinatesStayInGameSpaceOnlyTheExtractGuideIsBaseSpace()
    {
        var map = Catalog.Find("customs")!;
        var game = new GamePosition(100, 0, 200);
        var basePoint = map.ToBase(game);

        var player = new PlayerOverlay { Map = map };
        var extract = new ExtractLineOverlay
        {
            Map = map,
            Target = new MapPoi
            {
                Kind = PoiKind.ExtractPmc,
                Id = "ex_0",
                Name = "Crossroads",
                NameZh = "Crossroads",
                Position = game,
                Base = basePoint,
            },
            PlayerPosition = new GamePosition(0, 0, 0),
        };

        var pois = new PoiOverlay { Map = map };
        pois.SetPois(
        [
            new MapPoi
            {
                Kind = PoiKind.LootContainer,
                Name = "Loot",
                Position = game,
                Base = basePoint,
            },
        ]);

        var quests = new QuestOverlay { Map = map };
        quests.SetMarks(
        [
            new QuestMark("t1", "Task", "o1", "Objective", SKColors.Red, game, 0, false, []),
        ]);

        var annotations = new AnnotationOverlay { Map = map };
        var waypoints = new WaypointOverlay();
        waypoints.Add(game, basePoint);

        using var party = new PartySession();

        var snapshot = SnapshotBuilder.Build(
            map,
            player,
            extract,
            pois,
            quests,
            annotations,
            waypoints,
            party,
            [],
            new WebSyncSections(ShowPois: true, ShowQuests: true, ShowAnnotations: true, FollowPlayerFloor: false),
            inRaid: true,
            generatedAt: DateTime.UtcNow);

        // The projection actually moves this point, so game space and base space are distinguishable.
        Assert.NotEqual(game.X, basePoint.X);
        Assert.NotEqual(game.Z, basePoint.Y);

        var waypoint = Assert.Single(snapshot.Waypoints);
        Assert.Equal(game.X, waypoint.X);
        Assert.Equal(game.Z, waypoint.Z);

        var poi = Assert.Single(snapshot.Pois);
        Assert.Equal(game.X, poi.X);
        Assert.Equal(game.Z, poi.Z);

        var quest = Assert.Single(snapshot.Quests);
        Assert.Equal(game.X, quest.X);
        Assert.Equal(game.Z, quest.Z);

        // The one exception: the guide endpoint is already projected into base space.
        Assert.Equal(basePoint.X, snapshot.Extract!.GuideBase!.X, 6);
        Assert.Equal(basePoint.Y, snapshot.Extract.GuideBase.Y, 6);
    }

    [Fact]
    public void PlayerScreenAngleIsTakenFromTheProjectionRatherThanTheRawYaw()
    {
        var map = Catalog.Find("customs")!;
        var player = new PlayerOverlay { Map = map };
        player.Add(Fix(100, 200, yaw: 30));

        var built = SnapshotBuilder.BuildPlayer(map, player)!;

        // Customs is rotated 180 degrees, so folding the rotation in turns 30 into 210. A client
        // that recomputed from the raw yaw would point the marker the opposite way.
        Assert.Equal(210.0, built.ScreenAngleDegrees, 6);
        Assert.Equal(map.Projection.ScreenAngleDegrees(30), built.ScreenAngleDegrees, 6);
        Assert.NotEqual(MapProjection.Normalize360(30), built.ScreenAngleDegrees);
    }

    [Fact]
    public void ThePoiToggleEmptiesOnlyThePoiList()
    {
        var map = Catalog.Find("customs")!;
        var game = new GamePosition(100, 0, 200);
        var basePoint = map.ToBase(game);

        var player = new PlayerOverlay { Map = map };
        var extract = new ExtractLineOverlay { Map = map };

        var pois = new PoiOverlay { Map = map };
        pois.SetPois(
        [
            new MapPoi { Kind = PoiKind.LootContainer, Name = "Loot", Position = game, Base = basePoint },
        ]);

        var quests = new QuestOverlay { Map = map };
        var annotations = new AnnotationOverlay { Map = map };
        var waypoints = new WaypointOverlay();
        waypoints.Add(game, basePoint);

        using var party = new PartySession();

        var shown = SnapshotBuilder.Build(
            map, player, extract, pois, quests, annotations, waypoints, party, [],
            new WebSyncSections(ShowPois: true, ShowQuests: true, ShowAnnotations: true, FollowPlayerFloor: false),
            false, DateTime.UtcNow);

        var hidden = SnapshotBuilder.Build(
            map, player, extract, pois, quests, annotations, waypoints, party, [],
            new WebSyncSections(ShowPois: false, ShowQuests: true, ShowAnnotations: true, FollowPlayerFloor: false),
            false, DateTime.UtcNow);

        Assert.Single(shown.Pois);
        Assert.Empty(hidden.Pois);

        // The route is not part of the content filter and must survive the toggle untouched.
        Assert.Single(shown.Waypoints);
        Assert.Single(hidden.Waypoints);
    }

    // ---- 4. StateBroadcaster throttle ---------------------------------------

    /// <summary>
    /// The 2 Hz fix-coalescing: a burst of fixes inside one throttle window must reach the client
    /// as a single <c>player</c> message, not one message per fix.
    /// </summary>
    /// <remarks>
    /// Driven headlessly through <c>MapSession.ApplyFixForTesting</c>, which raises the same
    /// <c>FixApplied</c> event the screenshot watcher would. The client is a counting fake socket,
    /// so no port is bound and no Kestrel host is started.
    /// </remarks>
    [Fact]
    public void TheFixStreamIsCoalescedToTwoHertz()
    {
        const int fixes = 20;

        var settings = new AppSettings
        {
            AllowNetwork = false,
            ReadGameLog = false,
            SuggestMapFromPosition = false,
            CullMode = CullMode.Off,
            CurrentMap = "customs",
            ScreenshotFolder = Path.Combine(Path.GetTempPath(), "tmc-websync-throttle", Guid.NewGuid().ToString("N")),
        };

        using var session = new MapSession(settings, Catalog);
        using var broadcaster = new StateBroadcaster(session, settings);
        var socket = new CountingSocket();

        broadcaster.Register(socket);

        // Register sends the first frame inline; it must not be mistaken for a fix message.
        Assert.Equal(1, socket.CountOf("map"));

        for (var i = 0; i < fixes; i++)
            session.ApplyFixForTesting(Fix(0, 0, yaw: i));

        // One short wait past the 500 ms throttle window: enough for the single coalesced flush,
        // with no polling and no second window because no further fixes are injected.
        Thread.Sleep(700);

        var players = socket.CountOf("player");

        Assert.InRange(players, 1, 2);
        Assert.True(players < fixes, $"expected coalescing, saw {players} of {fixes}");
    }

    /// <summary>
    /// A <c>WebSocket</c> that records every frame the broadcaster sends, so the throttle is
    /// observable without binding a port. It reports itself open and completes each send inline.
    /// </summary>
    private sealed class CountingSocket : WebSocket
    {
        private readonly object _gate = new();
        private readonly List<JsonElement> _frames = [];

        /// <summary>How many sent frames carried the given <c>type</c> discriminator.</summary>
        internal int CountOf(string type)
        {
            lock (_gate)
                return _frames.Count(frame => frame.GetProperty("type").GetString() == type);
        }

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => WebSocketState.Open;

        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override void Dispose()
        {
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true));

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            var json = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
            var frame = JsonDocument.Parse(json).RootElement.Clone();

            lock (_gate)
                _frames.Add(frame);

            return Task.CompletedTask;
        }
    }
}
