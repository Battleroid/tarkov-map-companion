using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using TarkovMapCompanion.Localization;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Settings;
using TarkovMapCompanion.WebSync;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// The M1 WebSync server: token gating, the <c>/api/state</c> body, bind failure and the bounded
/// stop.
/// </summary>
/// <remarks>
/// Every host here binds port 0 so the OS picks a free port -- the CI runner is shared and a fixed
/// port 8765 would collide and flake. The server's reconciler is asynchronous, so a test that wants
/// to talk to it waits on <see cref="WebSyncServer.WaitUntilStarted"/>; a test about failure waits on
/// the status callback instead.
/// </remarks>
public sealed class WebSyncServerTests
{
    private const string Token = "ABCD2345";

    [Fact]
    public async Task CorrectKeyServesTheSnapshot()
    {
        using var harness = new Harness();
        harness.Start();

        using var http = new HttpClient();
        var response = await http.GetAsync(Url(harness, $"/api/state?key={Token}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task MissingKeyIsNotFound()
    {
        using var harness = new Harness();
        harness.Start();

        using var http = new HttpClient();
        var response = await http.GetAsync(Url(harness, "/api/state"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WrongKeyIsNotFound()
    {
        using var harness = new Harness();
        harness.Start();

        using var http = new HttpClient();
        var response = await http.GetAsync(Url(harness, "/api/state?key=ZZZZZZZZ"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RootWithoutAKeyStillServesTheHintPage()
    {
        using var harness = new Harness();
        harness.Start();

        using var http = new HttpClient();
        var response = await http.GetAsync(Url(harness, "/"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(Localizer.Loc("WebSync.ClientHintNeedKey"), body);
    }

    [Fact]
    public async Task RootWithTheKeyServesThePlaceholder()
    {
        using var harness = new Harness();
        harness.Start();

        using var http = new HttpClient();
        var response = await http.GetAsync(Url(harness, $"/?key={Token}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task StateBodyDeserializesWithVersionOne()
    {
        using var harness = new Harness();
        harness.Start();

        using var http = new HttpClient();
        var json = await http.GetStringAsync(Url(harness, $"/api/state?key={Token}"));

        var snapshot = JsonSerializer.Deserialize(json, WebSyncJsonContext.Default.WebSyncSnapshot);

        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot!.Version);
    }

    [Fact]
    public void BoundPortIsAssignedWhenTheRequestedPortIsZero()
    {
        using var harness = new Harness();
        harness.Start();

        Assert.NotNull(harness.Server.BoundPort);
        Assert.True(harness.Server.BoundPort > 0);
    }

    [Fact]
    public async Task PortConflictReportsStatusInsteadOfThrowing()
    {
        // A raw socket held exclusively on the wildcard address, which is what Kestrel's
        // "0.0.0.0:{port}" bind needs. Loopback plus ExclusiveAddressUse does NOT conflict with a
        // wildcard bind on this platform, and a TcpListener on any:0 does not either (it sets
        // SO_REUSEADDR); holding the wildcard exclusively is what reproduces AddressInUse(10048).
        using var blocker = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        blocker.ExclusiveAddressUse = true;
        blocker.Bind(new IPEndPoint(IPAddress.Any, 0));
        blocker.Listen(1);

        var port = ((IPEndPoint)blocker.LocalEndPoint!).Port;

        using var harness = new Harness();
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.StatusReported += (_, message) => reported.TrySetResult(message);

        var exception = Record.Exception(() => harness.Server.Update(true, port, Token));

        Assert.Null(exception);

        var message = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Localizer.Loc("WebSync.StatusPortInUse", port), message);
        Assert.Null(harness.Server.BoundPort);
    }

    [Fact]
    public void DisposeIsBounded()
    {
        var harness = new Harness();
        harness.Start();

        var stopwatch = Stopwatch.StartNew();
        harness.Server.Dispose();
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2.5),
            $"Dispose took {stopwatch.Elapsed}");

        // The host was actually stopped, not merely abandoned: the port is released.
        Assert.Null(harness.Server.BoundPort);

        // A second dispose is a no-op, and the harness cleans up the rest.
        harness.Server.Dispose();
        harness.Dispose();
    }

    private static string Url(Harness harness, string path) =>
        $"http://127.0.0.1:{harness.Server.BoundPort}{path}";

    /// <summary>
    /// A real session, broadcaster and server, assembled the way <c>MapSession</c> assembles them,
    /// so the tests exercise the actual wiring rather than a stand-in.
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
                ScreenshotFolder = Path.Combine(Path.GetTempPath(), "tmc-websync-noshots"),
                WebSyncEnabled = false,
                WebSyncAccessToken = Token,
            };

            Session = new MapSession(Settings, MapCatalog.LoadEmbedded());
            Broadcaster = new StateBroadcaster(Session, Settings);
            Server = new WebSyncServer(Broadcaster, Settings, message =>
            {
                lock (Statuses)
                    Statuses.Add(message);

                StatusReported?.Invoke(this, message);
            });
        }

        public AppSettings Settings { get; }

        public MapSession Session { get; }

        public StateBroadcaster Broadcaster { get; }

        public WebSyncServer Server { get; }

        public List<string> Statuses { get; } = [];

        public event EventHandler<string>? StatusReported;

        /// <summary>Enables the server on an OS-assigned port and waits until it is listening.</summary>
        public void Start()
        {
            Server.Update(enabled: true, port: 0, Token);

            Assert.True(
                Server.WaitUntilStarted(TimeSpan.FromSeconds(5)),
                "the WebSync server did not start within 5s");
        }

        public void Dispose()
        {
            Broadcaster.Dispose();
            Server.Dispose();
            Session.Dispose();
        }
    }
}
