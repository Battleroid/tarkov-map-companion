using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Screenshots;
using TarkovMapCompanion.Settings;
using TarkovMapCompanion.WebSync;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// The M3 WebSocket endpoint (<c>WS /ws</c>): first-frame contract, incremental delivery, token
/// rejection, the 4 KB frame cap and clean unregistration.
/// </summary>
/// <remarks>
/// Every host binds port 0 so the OS assigns a free port -- the CI runner is shared and a fixed port
/// would collide and flake. A real <see cref="ClientWebSocket"/> talks to a real Kestrel over
/// loopback, so this exercises the actual upgrade path rather than a fake socket. Timeouts are
/// generous but bounded; no fixed port is ever used.
/// </remarks>
public sealed class WebSyncWsTests
{
    private const string Token = "ABCD2345";

    private static readonly MapCatalog Catalog = MapCatalog.LoadEmbedded();

    // ---- 1. First frame -----------------------------------------------------

    /// <summary>
    /// Connecting with the right key yields exactly the full snapshot as the first frame, as a
    /// <c>map</c> message carrying a non-null <c>snapshot</c> at version 1.
    /// </summary>
    [Fact]
    public async Task FirstFrameIsTheFullSnapshot()
    {
        using var harness = new Harness();
        harness.Start();

        using var socket = await ConnectAsync(harness, Token);

        var message = Parse(await ReceiveTextAsync(socket, TimeSpan.FromSeconds(5)));

        Assert.Equal("map", message.Type);
        Assert.NotNull(message.Snapshot);
        Assert.Equal(1, message.Snapshot!.Version);
        Assert.NotNull(message.Snapshot.Map);
    }

    // ---- 2. Incremental arrival ---------------------------------------------

    /// <summary>
    /// A state change that the broadcaster coalesces into a <c>player</c> message reaches the
    /// connected client, driven exactly the way the throttle test drives it.
    /// </summary>
    [Fact]
    public async Task IncrementalPlayerMessageArrives()
    {
        using var harness = new Harness();
        harness.Start();

        using var socket = await ConnectAsync(harness, Token);

        // Drain the first frame so any later message is unambiguously incremental.
        var first = Parse(await ReceiveTextAsync(socket, TimeSpan.FromSeconds(5)));
        Assert.Equal("map", first.Type);

        // A point at the centre of the map's game bounds is unambiguously inside it, so the fix
        // cannot trip the "suggest another map" path and change what is being mirrored.
        var bounds = Catalog.Find("customs")!.Bounds;
        var x = (bounds[0][0] + bounds[1][0]) / 2.0;
        var z = (bounds[0][1] + bounds[1][1]) / 2.0;

        harness.Session.ApplyFixForTesting(Fix(x, z, yaw: 90));

        // The broadcaster's 500 ms throttle must elapse before the player message flushes.
        var player = await ReceiveUntilAsync(socket, "player", TimeSpan.FromSeconds(5));

        Assert.NotNull(player.Player);
        Assert.Equal(x, player.Player!.X, 6);
        Assert.Equal(z, player.Player.Z, 6);
    }

    // ---- 3. Token rejection -------------------------------------------------

    /// <summary>
    /// A missing or mismatched key never upgrades: the token gate answers 404, and a real WebSocket
    /// client sees a non-101 and throws instead of connecting.
    /// </summary>
    [Fact]
    public async Task MissingOrWrongKeyIsRejected()
    {
        using var harness = new Harness();
        harness.Start();

        // The HTTP status is directly observable, before any upgrade is attempted.
        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(harness.Url("/ws"))).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await http.GetAsync(harness.Url("/ws?key=ZZZZZZZZ"))).StatusCode);

        // The right key passes the gate; a non-upgrade GET is then a plain 400 from the handler.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await http.GetAsync(harness.Url($"/ws?key={Token}"))).StatusCode);

        using (var noKey = new ClientWebSocket())
        {
            await Assert.ThrowsAnyAsync<WebSocketException>(
                () => noKey.ConnectAsync(new Uri(harness.WsUrl(null)), CancellationToken.None));
        }

        using (var wrongKey = new ClientWebSocket())
        {
            await Assert.ThrowsAnyAsync<WebSocketException>(
                () => wrongKey.ConnectAsync(new Uri(harness.WsUrl("ZZZZZZZZ")), CancellationToken.None));
        }
    }

    // ---- 4. 4 KB frame cap --------------------------------------------------

    /// <summary>
    /// A client text frame larger than the 4 KB buffer closes the connection with 1009.
    /// </summary>
    [Fact]
    public async Task OversizedFrameClosesWithMessageTooBig()
    {
        using var harness = new Harness();
        harness.Start();

        using var socket = await ConnectAsync(harness, Token);
        Assert.Equal("map", Parse(await ReceiveTextAsync(socket, TimeSpan.FromSeconds(5))).Type);

        // 5000 bytes in one frame, over the 4096-byte cap.
        var oversized = Encoding.UTF8.GetBytes(new string('x', 5000));
        await socket.SendAsync(
            new ArraySegment<byte>(oversized),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);

        var buffer = new byte[64];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        WebSocketReceiveResult result;
        try
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
        }
        catch (WebSocketException)
        {
            // Some stacks surface the server's abort as an exception rather than a close frame.
            // Either way the connection did not survive the oversized frame; that is the contract.
            return;
        }

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, result.CloseStatus);
    }

    // ---- 5. Clean unregistration --------------------------------------------

    /// <summary>
    /// One client's disconnect must not take the host with it: after the first client closes
    /// cleanly, a second client still receives its full-snapshot first frame.
    /// </summary>
    [Fact]
    public async Task OneClientDisconnectDoesNotKillTheHost()
    {
        using var harness = new Harness();
        harness.Start();

        var first = await ConnectAsync(harness, Token);
        Assert.Equal("map", Parse(await ReceiveTextAsync(first, TimeSpan.FromSeconds(5))).Type);

        // A clean close handshake: the server sees Close, answers it, and unregisters.
        using (var close = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, null, close.Token);
        }

        first.Dispose();

        // The host is still healthy and serving new connections.
        using var second = await ConnectAsync(harness, Token);
        var message = Parse(await ReceiveTextAsync(second, TimeSpan.FromSeconds(5)));

        Assert.Equal("map", message.Type);
        Assert.NotNull(message.Snapshot);
    }

    // ---- Helpers ------------------------------------------------------------

    private static PlayerFix Fix(double x, double z, double yaw = 0) => new()
    {
        Position = new GamePosition(x, 0, z),
        YawDegrees = yaw,
        Rotation = (0, 0, 0, 1),
        RaidTimeHours = 10,
        TakenAt = new DateTime(2026, 8, 7, 19, 31, 0, DateTimeKind.Utc),
        FilePath = $@"C:\s\{x}-{z}.png",
    };

    private static async Task<ClientWebSocket> ConnectAsync(Harness harness, string? key)
    {
        var socket = new ClientWebSocket();

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(new Uri(harness.WsUrl(key)), timeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, TimeSpan timeout)
    {
        var buffer = new byte[16 * 1024];
        using var cancellation = new CancellationTokenSource(timeout);

        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation.Token);

        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage, "the server sends each message as a single text frame");

        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    /// <summary>
    /// Reads messages until one carries the wanted discriminator, so incidental broadcasts that race
    /// in ahead of the one under test (a <c>meta</c> from a party publish, say) do not make it flaky.
    /// </summary>
    private static async Task<WebSyncMessage> ReceiveUntilAsync(
        ClientWebSocket socket,
        string type,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"no '{type}' message arrived within {timeout}");

            var message = Parse(await ReceiveTextAsync(socket, remaining));
            if (message.Type == type)
                return message;
        }
    }

    private static WebSyncMessage Parse(string json) =>
        JsonSerializer.Deserialize(json, WebSyncJsonContext.Default.WebSyncMessage)!;

    /// <summary>
    /// A real session, broadcaster and server, wired the way <c>MapSession</c> wires them, so the
    /// tests exercise the actual endpoints rather than a stand-in.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Settings = new AppSettings
            {
                AllowNetwork = false,
                CurrentMap = "customs",
                CullMode = CullMode.Off,
                ScreenshotFolder = Path.Combine(Path.GetTempPath(), "tmc-websync-ws-noshots"),
                WebSyncEnabled = false,
                WebSyncAccessToken = Token,
            };

            Session = new MapSession(Settings, Catalog);
            Broadcaster = new StateBroadcaster(Session, Settings);
            Server = new WebSyncServer(
                Broadcaster,
                Settings,
                new AssetCache { AllowNetwork = false },
                Catalog,
                _ => { });
        }

        public AppSettings Settings { get; }

        public MapSession Session { get; }

        public StateBroadcaster Broadcaster { get; }

        public WebSyncServer Server { get; }

        /// <summary>Enables the server on an OS-assigned port and waits until it is listening.</summary>
        public void Start()
        {
            Server.Update(enabled: true, port: 0, Token);

            Assert.True(
                Server.WaitUntilStarted(TimeSpan.FromSeconds(5)),
                "the WebSync server did not start within 5s");
        }

        public string Url(string path) => $"http://127.0.0.1:{Server.BoundPort}{path}";

        public string WsUrl(string? key) =>
            $"ws://127.0.0.1:{Server.BoundPort}/ws" + (key is null ? "" : $"?key={key}");

        public void Dispose()
        {
            Broadcaster.Dispose();
            Server.Dispose();
            Session.Dispose();
        }
    }
}
