using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Diagnostics;
using TarkovMapCompanion.Localization;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.WebSync;

/// <summary>
/// Hosts the embedded Kestrel server that mirrors the desktop map to a browser on the LAN.
/// </summary>
/// <remarks>
/// <para>
/// The server owns no drawing state and never writes anything: it serves the snapshot the
/// <see cref="StateBroadcaster"/> builds and, later, the map imagery the rest of the app already
/// caches. M1 exposes only <c>GET /</c> (token-exempt, a placeholder) and <c>GET /api/state</c>
/// (token-gated JSON), plus the token middleware every future endpoint inherits.
/// </para>
/// <para>
/// <b>Threading.</b> Kestrel's <c>Build</c>/<c>StartAsync</c>/<c>StopAsync</c> capture the ambient
/// <see cref="SynchronizationContext"/> and deadlock if it is never pumped, which is exactly what
/// the Avalonia UI thread does. So every host call happens on a single dedicated worker thread that
/// installs a null context. <see cref="Update"/> only records the desired state and returns, so it
/// is safe to call from the UI thread.
/// </para>
/// <para>
/// <b>Ownership.</b> Constructed by <see cref="MapSession"/> at the end of its constructor and
/// never started there; <see cref="Update"/> turns it on. <see cref="Dispose"/> stops it with a
/// bounded wait and is idempotent.
/// </para>
/// </remarks>
internal sealed class WebSyncServer : IDisposable
{
    /// <summary>How long a stop may take before the server gives up and lets the OS reclaim the socket.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Logical name of the embedded single-page browser client under <c>WebSync/Assets/</c>.</summary>
    private const string ClientPageResourceName = "TarkovMapCompanion.WebSync.Assets.index.html";

    /// <summary>
    /// The largest client frame this endpoint will read. A message that does not fit the fixed buffer
    /// closes the connection with 1009 (message too big): the mirror is read-only, so nothing a
    /// client sends is worth buffering.
    /// </summary>
    private const int ClientFrameCapBytes = 4 * 1024;

    /// <summary>
    /// How often Kestrel pings each WebSocket. The ping is emitted by Kestrel's transport (via
    /// <c>WebSocketOptions.KeepAliveInterval</c>), not by this endpoint, so it never competes with the
    /// broadcaster's application sends for the socket's single writer slot.
    /// </summary>
    private static readonly TimeSpan ServerPingInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// No client frame within this window (roughly two missed server pings' worth of silence) means
    /// the browser is gone; the socket is unregistered and disposed rather than pinned forever.
    /// </summary>
    private static readonly TimeSpan ClientLivenessTimeout = TimeSpan.FromSeconds(60);

    private readonly StateBroadcaster _broadcaster;
    private readonly AppSettings _settings;
    private readonly AssetCache _assets;
    private readonly MapCatalog _catalog;

    /// <summary>Reports a start failure to the UI (maps to <see cref="MapSession.Status"/>).</summary>
    private readonly Action<string> _reportStatus;

    /// <summary>
    /// Filtered SVG text, keyed by map id, base flag and the sorted layer ids (section 6.1 of the
    /// spec). Bounded by map count times the handful of floor combinations a session asks for, so no
    /// eviction is needed: entries live for the process and are recomputed on the next request anyway.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _svgCache = new(StringComparer.Ordinal);

    /// <summary>Guards the desired state, the worker handle and the disposal flag.</summary>
    private readonly object _gate = new();

    // ---- Desired state: written by the UI thread, read by the worker ------------------------------

    private bool _desiredEnabled;
    private int _desiredPort;

    /// <summary>Set when a new desired state awaits the worker; cleared as the worker picks it up.</summary>
    private bool _pending;

    // ---- Worker and live host: touched only by the worker after construction ----------------------

    private Thread? _worker;

    /// <summary>The live host, or null when not serving. Only ever assigned on the worker thread.</summary>
    private WebApplication? _host;

    /// <summary>The port the live host was asked for; the actual port may differ when it was 0.</summary>
    private int _runningPort;

    /// <summary>The actually bound port, or 0 when not serving. Published for tests and the UI.</summary>
    private int _boundPort;

    private bool _disposed;

    /// <summary>
    /// Creates the server. Nothing is built or bound here; call <see cref="Update"/> to start.
    /// </summary>
    /// <param name="broadcaster">Builds the snapshot served by <c>GET /api/state</c>.</param>
    /// <param name="settings">Read live, so a regenerated token takes effect without a restart.</param>
    /// <param name="assets">Backs the cached SVG maps and tile pyramids the imagery routes serve.</param>
    /// <param name="catalog">Whitelists <c>{mapId}</c> and supplies each map's floors and templates.</param>
    /// <param name="reportStatus">Surfaces a bind failure; never receives the token.</param>
    internal WebSyncServer(
        StateBroadcaster broadcaster,
        AppSettings settings,
        AssetCache assets,
        MapCatalog catalog,
        Action<string> reportStatus)
    {
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _reportStatus = reportStatus ?? throw new ArgumentNullException(nameof(reportStatus));
    }

    /// <summary>
    /// The port the server is actually listening on, or null when it is not serving.
    /// </summary>
    /// <remarks>
    /// Populated by parsing the address feature after a successful start, so a requested port of 0
    /// reports the OS-assigned one instead of 0.
    /// </remarks>
    internal int? BoundPort
    {
        get
        {
            var port = Volatile.Read(ref _boundPort);
            return port > 0 ? port : null;
        }
    }

    /// <summary>
    /// Records the desired run state and wakes the reconciler. Returns immediately.
    /// </summary>
    /// <remarks>
    /// Safe to call from any thread, including the UI thread: no build, bind or await happens here.
    /// A disabled request stops a running host; an enabled request starts or (on a port change)
    /// restarts one. The token is not part of the desired state because it is read from
    /// <see cref="AppSettings"/> on every request, so regenerating it needs no restart.
    /// </remarks>
    /// <param name="enabled">Whether the LAN mirror should be serving.</param>
    /// <param name="port">The port to bind; 0 asks the OS to pick one.</param>
    /// <param name="token">The current token, recorded only so the API shape matches future use.</param>
    internal void Update(bool enabled, int port, string token)
    {
        _ = token;

        lock (_gate)
        {
            if (_disposed)
                return;

            _desiredEnabled = enabled;
            _desiredPort = port;
            _pending = true;

            // Started lazily so a session that never enables WebSync never owns a thread.
            if (_worker is null)
            {
                _worker = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "WebSync reconciler",
                };
                _worker.Start();
            }

            Monitor.Pulse(_gate);
        }
    }

    /// <summary>Blocks until the server is listening or the timeout elapses. A test hook.</summary>
    /// <remarks>
    /// The reconciler is asynchronous by design, so this is the smallest way for a test (or a
    /// status poll) to observe that the bind finished. Never called on the UI path.
    /// </remarks>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>True when a bound port was observed.</returns>
    internal bool WaitUntilStarted(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (BoundPort is > 0)
                return true;

            Thread.Sleep(20);
        }

        return BoundPort is > 0;
    }

    /// <summary>
    /// Stops the host with a bounded wait and becomes inert. Idempotent.
    /// </summary>
    /// <remarks>
    /// Called from <see cref="MapSession.Dispose"/> on the UI thread, so the join is bounded: a
    /// wedged start must not be able to hold the window open. The worker's own stop is bounded too,
    /// and the host's threads are background threads, so a timeout costs an orphaned listener at
    /// most, reclaimed by the OS when the process exits.
    /// </remarks>
    public void Dispose()
    {
        Thread? worker;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            worker = _worker;
            Monitor.Pulse(_gate);
        }

        // The worker owns the host: on seeing the flag it stops and disposes it, then exits.
        worker?.Join(StopTimeout);
    }

    // ---- Reconciler -------------------------------------------------------------------------------

    /// <summary>
    /// The single place a host is ever built, started, stopped or disposed.
    /// </summary>
    /// <remarks>
    /// Runs on a thread with no <see cref="SynchronizationContext"/>, so Kestrel's async calls can
    /// be waited on without capturing a context that never pumps. Because it is the only writer of
    /// <see cref="_host"/>, there can never be two live hosts and a disposed host is never restarted.
    /// </remarks>
    private void WorkerLoop()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        while (true)
        {
            bool enabled;
            int port;
            bool stopping;

            lock (_gate)
            {
                // Wait for work; "latest desired state wins" falls out of always reading the newest
                // values here, so a burst of enable/disable/enable requests converges on the last one.
                while (!_pending && !_disposed)
                    Monitor.Wait(_gate);

                stopping = _disposed;
                _pending = false;
                enabled = _desiredEnabled;
                port = _desiredPort;
            }

            if (stopping)
            {
                // Dispose's final request: release the listening socket before the thread exits, so
                // the port is free even though Dispose only waited on the join bounded.
                StopHost();
                return;
            }

            try
            {
                Reconcile(enabled, port);
            }
            catch (Exception ex)
            {
                // A reconcile must never kill the worker; the next Update gets a fresh chance.
                Log.Error("websync reconcile failed", ex);
            }
        }
    }

    /// <summary>Converges the live host to the desired enabled/port pair.</summary>
    private void Reconcile(bool enabled, int port)
    {
        if (!enabled)
        {
            StopHost();
            return;
        }

        // Already serving the requested port. The token is read live from settings per request, so
        // a regenerated token does not force a rebuild.
        if (_host is not null && _runningPort == port)
            return;

        // A different port, or nothing running yet: tear the old one down first so two hosts can
        // never hold the same process at once.
        StopHost();
        StartHost(port);
    }

    private void StartHost(int port)
    {
        WebApplication? app = null;

        try
        {
            app = BuildHost(port);

            // On the worker thread with a null context, so this cannot deadlock the UI thread.
            app.StartAsync().GetAwaiter().GetResult();

            var bound = ResolveBoundPort(app, port);

            _host = app;
            _runningPort = port;
            Volatile.Write(ref _boundPort, bound);

            Log.Info($"websync listening on port {bound}");
        }
        catch (IOException ex)
        {
            // The empirically established bind-failure exception: IOException wrapping
            // AddressInUseException wrapping SocketException(10048). Report, never crash.
            Log.Warn($"websync could not bind port {port}: {ex.Message}");
            SafeDisposeApp(app);
            _host = null;
            _runningPort = 0;
            Volatile.Write(ref _boundPort, 0);
            _reportStatus(Localizer.Loc("WebSync.StatusPortInUse", port));
        }
        catch (Exception ex)
        {
            Log.Error($"websync failed to start on port {port}", ex);
            SafeDisposeApp(app);
            _host = null;
            _runningPort = 0;
            Volatile.Write(ref _boundPort, 0);
        }
    }

    private void StopHost()
    {
        var app = _host;
        _host = null;
        _runningPort = 0;
        Volatile.Write(ref _boundPort, 0);

        if (app is null)
            return;

        try
        {
            // Bounded: M1 has no WebSocket clients yet, so this is normally a few milliseconds. In
            // M3 the broadcaster aborts its sockets before this runs, which is what keeps it short.
            if (app.StopAsync().Wait(StopTimeout))
                Log.Info("websync stopped");
            else
                Log.Warn("websync did not stop within the timeout; the OS will reclaim the socket");
        }
        catch (Exception ex)
        {
            Log.Warn($"websync stop failed: {ex.Message}");
        }
        finally
        {
            SafeDisposeApp(app);
        }
    }

    private static void SafeDisposeApp(WebApplication? app)
    {
        if (app is null)
            return;

        try
        {
            if (!app.DisposeAsync().AsTask().Wait(StopTimeout))
                Log.Warn("websync host disposal did not complete within the timeout");
        }
        catch (Exception ex)
        {
            Log.Warn($"websync host disposal failed: {ex.Message}");
        }
    }

    // ---- Host construction ------------------------------------------------------------------------

    private WebApplication BuildHost(int port)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // Kestrel's own chatter would otherwise reach the app log; our start/stop/error lines go
        // through Diagnostics.Log instead.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = StopTimeout);

        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        var app = builder.Build();

        // Token middleware first, so it fronts every endpoint mapped below and any added later.
        // The WS upgrade carries the token as ?key= because a browser cannot set headers on it.
        app.Use(TokenGate);

        // WebSockets must be enabled for HttpContext.WebSockets to accept an upgrade. The keep-alive
        // interval is the server heartbeat (section 7.1): Kestrel pings every 30s in the transport,
        // so a dead peer is eventually noticed without this endpoint writing on the socket.
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = ServerPingInterval });

        app.MapGet("/", HandleRoot);
        app.MapGet("/api/state", HandleState);
        app.MapGet("/map/{mapId}.svg", HandleMapSvg);
        app.MapGet("/tiles/{mapId}/{z:int}/{x:int}/{y:int}.png", HandleTile);
        app.Map("/ws", (RequestDelegate)HandleWebSocket);

        return app;
    }

    /// <summary>
    /// Rejects any request that does not carry the current token, except the static entry points.
    /// </summary>
    /// <remarks>
    /// A missing or mismatched key answers 404 rather than 401/403, so an unauthenticated visitor
    /// cannot even tell a service exists here. Case-sensitive, exact compare; the token is read from
    /// the live settings object so a regenerated one takes effect immediately.
    /// </remarks>
    private async Task TokenGate(HttpContext context, RequestDelegate next)
    {
        // Every response says not to sniff it, including the 404s and the token-exempt page.
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        if (TokenExempt(context.Request) || TokenMatches(context.Request.Query["key"].ToString()))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    /// <summary>True for the two paths that never require a key: <c>GET /</c> and <c>GET /assets/*</c>.</summary>
    private static bool TokenExempt(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
            return false;

        var path = request.Path;

        if (path == "/" || path == string.Empty)
            return true;

        return path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase);
    }

    private bool TokenMatches(string provided)
    {
        var expected = _settings.WebSyncAccessToken;

        // No token generated yet means nothing is authorised: an empty key must never match.
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
            return false;

        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(expected);

        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    // ---- Endpoints --------------------------------------------------------------------------------

    /// <summary>
    /// The entry point a tablet opens. Token-exempt so a wrong link gets a readable hint instead of
    /// a bare 404. With a matching key it serves the embedded single-page client when that asset is
    /// present, falling back to a minimal "running" placeholder while the asset lane has not landed
    /// it yet, so the endpoint is correct with or without the file.
    /// </summary>
    private IResult HandleRoot(HttpContext context)
    {
        if (TokenMatches(context.Request.Query["key"].ToString()))
        {
            if (TryReadClientPage(out var clientPage))
                return Results.Content(clientPage, "text/html; charset=utf-8");

            return Results.Content(
                "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
                + "<title>WebSync</title></head>"
                + "<body><p>WebSync is running.</p></body></html>",
                "text/html; charset=utf-8");
        }

        var hint = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            + "<title>WebSync</title></head><body><p>"
            + WebUtility.HtmlEncode(Localizer.Loc("WebSync.ClientHintNeedKey"))
            + "</p></body></html>";

        return Results.Content(hint, "text/html; charset=utf-8");
    }

    /// <summary>
    /// Reads the embedded browser client, or reports false when it is absent or unreadable.
    /// </summary>
    /// <remarks>
    /// Uses the same <see cref="Assembly.GetManifestResourceStream(string)"/> idiom as
    /// <see cref="MapCatalog"/> and <c>ExtractNotesStore</c>. The resource is owned by the parallel
    /// client lane, so a missing file is a normal state here, not an error: the caller falls back to
    /// the placeholder page and the endpoint keeps working.
    /// </remarks>
    private static bool TryReadClientPage(out string html)
    {
        html = "";

        try
        {
            using var stream = Assembly
                .GetExecutingAssembly()
                .GetManifestResourceStream(ClientPageResourceName);

            if (stream is null)
                return false;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            html = reader.ReadToEnd();
            return html.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"websync could not read the embedded client page: {ex.Message}");
            return false;
        }
    }

    // ---- WebSocket (section 7.1) ------------------------------------------------------------------

    /// <summary>
    /// The read-only event stream: registers the socket with the broadcaster (which sends the full
    /// snapshot as the first frame) and then runs the receive loop for liveness only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs entirely on the Kestrel request thread; nothing here touches the UI thread or the worker
    /// reconciler. The endpoint never writes on the socket itself -- all outbound traffic is the
    /// broadcaster's, serialized through its per-client gate -- so the only writer contention to
    /// reason about is the broadcaster's own fan-out.
    /// </para>
    /// <para>
    /// The token gate has already run: a bad or missing <c>?key=</c> was answered with 404 before this
    /// method, so <see cref="AcceptWebSocketAsync"/> is only reached for an authorised upgrade.
    /// </para>
    /// </remarks>
    private async Task HandleWebSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

        Log.Info("websync websocket client connected");

        // Register sends the full snapshot first (the first-frame contract lives in the broadcaster,
        // not here) and publishes the client for subsequent incremental broadcasts.
        _broadcaster.Register(socket);

        var connectedAt = DateTime.UtcNow;

        try
        {
            await ReceiveLoopAsync(socket, connectedAt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A receive/send failure must never escape onto the Kestrel request thread.
            Log.Warn($"websync websocket ended: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // The only unregistration path, so it happens exactly once however the loop ended.
            _broadcaster.Unregister(socket);
        }
    }

    /// <summary>
    /// Consumes the receive direction for liveness until the client closes, over-sends or goes silent.
    /// </summary>
    /// <remarks>
    /// The payload is discarded: the mirror is read-only, so a client frame means nothing beyond
    /// "still here". A message larger than the fixed buffer makes <c>EndOfMessage</c> false, which is
    /// the 4 KB cap the spec calls for, and closes with 1009. A 60s silence (two missed 30s pings)
    /// is treated as a dead peer. Each received frame and the loop's exit are logged so a client that
    /// connects but never gets its imagery can be told apart from one that never sends at all.
    /// </remarks>
    /// <param name="socket">The accepted, registered client socket.</param>
    /// <param name="connectedAt">When the upgrade was accepted, for the lifetime and inter-frame deltas.</param>
    private static async Task ReceiveLoopAsync(WebSocket socket, DateTime connectedAt)
    {
        var buffer = new byte[ClientFrameCapBytes];
        var frames = 0;
        var previousFrameAt = connectedAt;
        var endReason = "socket not open";

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;

                try
                {
                    // A fresh window per read: any complete frame resets the liveness clock.
                    using var timeout = new CancellationTokenSource(ClientLivenessTimeout);
                    result = await socket
                        .ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    endReason = "liveness timeout";
                    Log.Info("websync dropping a websocket client that went silent");
                    break;
                }

                frames++;
                var now = DateTime.UtcNow;
                var sincePreviousMs = (now - previousFrameAt).TotalMilliseconds;
                previousFrameAt = now;

                Log.Info(
                    $"websync websocket frame {result.MessageType} {result.Count} bytes; "
                    + $"{sincePreviousMs:F0} ms since previous frame");

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    endReason = "closed by peer";
                    await CloseOutputAsync(socket).ConfigureAwait(false);
                    break;
                }

                // EndOfMessage is false exactly when the message did not fit the 4 KB buffer: too big.
                if (!result.EndOfMessage)
                {
                    endReason = "message too big";
                    await CloseWithStatusAsync(socket, WebSocketCloseStatus.MessageTooBig).ConfigureAwait(false);
                    break;
                }

                // A complete text/binary frame (a heartbeat) is all this direction needs.
            }
        }
        catch
        {
            // Let the caller log the failure; this only names the loop's exit reason for the summary.
            endReason = "exception";
            throw;
        }
        finally
        {
            var lifetimeMs = (DateTime.UtcNow - connectedAt).TotalMilliseconds;
            Log.Info(
                $"websync websocket loop ended ({endReason}); frames={frames}; lifetime={lifetimeMs:F0} ms");
        }
    }

    /// <summary>Completes a client-initiated close handshake, swallowing a peer that already left.</summary>
    private static async Task CloseOutputAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket
                    .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException
                                      or ObjectDisposedException
                                      or OperationCanceledException)
        {
            // The peer is gone; disposal in Unregister is the fallback.
        }
    }

    /// <summary>Sends a close frame with the given status, swallowing an already-broken socket.</summary>
    private static async Task CloseWithStatusAsync(WebSocket socket, WebSocketCloseStatus status)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket
                    .CloseOutputAsync(status, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException
                                      or ObjectDisposedException
                                      or OperationCanceledException)
        {
            // The close frame could not be delivered; Unregister still disposes the socket.
        }
    }

    /// <summary>Serves the current full snapshot as JSON. Gated by the token middleware.</summary>
    private IResult HandleState(HttpContext context)
    {
        var snapshot = _broadcaster.BuildSnapshot();
        var json = JsonSerializer.Serialize(snapshot, WebSyncJsonContext.Default.WebSyncSnapshot);

        // State changes constantly and is scoped to one raid, so nothing may be cached.
        context.Response.Headers["Cache-Control"] = "no-store";

        return Results.Content(json, "application/json; charset=utf-8");
    }

    // ---- Map imagery (section 6 of the spec) ------------------------------------------------------

    /// <summary>
    /// Serves one map's SVG with only the requested floors present.
    /// </summary>
    /// <remarks>
    /// The <c>floors</c> query carries <see cref="MapFloor.Name"/> values, but the filter matches
    /// <see cref="MapFloor.SvgLayerId"/> -- two different namespaces. A name with no layer id (a
    /// tile-only floor, e.g. Customs' 4th) simply contributes nothing, and the base flag still applies.
    /// </remarks>
    private async Task<IResult> HandleMapSvg(HttpContext context, string mapId, CancellationToken ct)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        var floorsRaw = context.Request.Query["floors"].ToString();
        var baseRaw = context.Request.Query["base"].ToString();

        var map = FindMap(mapId);

        Log.Info($"websync map.svg request map={mapId} floors=\"{floorsRaw}\" base=\"{baseRaw}\"");

        // An unknown key or a map with no vector artwork: there is nothing here to serve.
        if (map is null || !map.HasSvg)
        {
            Log.Info($"websync map.svg -> 404 (unknown map or no svg) map={mapId}");
            return Results.NotFound();
        }

        var floorNames = ParseFloorNames(floorsRaw);
        var includeBase = ParseIncludeBase(baseRaw);

        var layerIds = map.Floors
            .Where(f => floorNames.Contains(f.Name) && f.SvgLayerId is not null)
            .Select(f => f.SvgLayerId!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        // Stable key: the same floors in a different order, or with duplicates, share one entry.
        var cacheKey = $"{mapId}|{includeBase}|{string.Join('|', layerIds)}";
        if (_svgCache.TryGetValue(cacheKey, out var cached))
        {
            Log.Info(
                $"websync map.svg -> cache hit map={mapId} bytes={Encoding.UTF8.GetByteCount(cached)}");
            return SvgResponse(cached, context);
        }

        Log.Info($"websync map.svg -> cache miss; fetching asset map={mapId} url={map.SvgUrl}");

        var started = Stopwatch.GetTimestamp();
        var text = await _assets.GetStringAsync(map.SvgUrl!, "svg", ct).ConfigureAwait(false);
        var fetchMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        Log.Info(
            $"websync map.svg asset fetched map={mapId} elapsed={fetchMs:F0} ms textNull={text is null}");

        if (text is null)
        {
            // Offline with nothing cached, or a transient upstream failure. Both mean "the imagery is
            // not available right now", which is a 503: a 404 would claim the map does not exist.
            Log.Info($"websync map.svg -> 503 (imagery unavailable) map={mapId}");
            return Results.Text(
                Localizer.Loc("WebSync.ImageryUnavailable"),
                "text/plain; charset=utf-8",
                Encoding.UTF8,
                StatusCodes.Status503ServiceUnavailable);
        }

        var filtered = SvgLayerFilter.Filter(text, map.BaseSvgLayerId, layerIds, includeBase);
        _svgCache[cacheKey] = filtered;

        Log.Info(
            $"websync map.svg -> 200 served map={mapId} bytes={Encoding.UTF8.GetByteCount(filtered)}");

        return SvgResponse(filtered, context);
    }

    /// <summary>
    /// Serves one raster tile, proxied from the same cache the desktop renderer uses.
    /// </summary>
    /// <remarks>
    /// The template is chosen by the shared <see cref="TileMapSource.ResolveTemplate"/> so the browser
    /// and the desktop are guaranteed to look at the same pyramid for the same floor.
    /// </remarks>
    private async Task<IResult> HandleTile(
        HttpContext context,
        string mapId,
        int z,
        int x,
        int y,
        CancellationToken ct)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        var map = FindMap(mapId);

        // An unknown key or a map with no pyramid: 404. A tile missing from a valid pyramid is not.
        if (map is null || !map.HasTiles)
            return Results.NotFound();

        // Outside the declared pyramid, or a coordinate that cannot exist at this zoom. The zoom
        // checks short-circuit before the shift, so an out-of-range z never reaches it.
        if (z < map.MinZoomLevel || z > map.MaxZoomLevel || x < 0 || y < 0 || x >= 1L << z || y >= 1L << z)
            return Results.NotFound();

        var floorName = context.Request.Query["floor"].ToString().Trim();
        var includeBase = ParseIncludeBase(context.Request.Query["base"].ToString());

        IReadOnlyCollection<string> floors = string.IsNullOrEmpty(floorName) ? [] : [floorName];
        var template = TileMapSource.ResolveTemplate(map, floors, includeBase);

        // No template (base hidden and no floor selected) counts as a missing tile: a quiet 1x1
        // rather than an error the browser would spray into the console for every empty cell.
        if (template is null)
            return TransparentPixel(context);

        var url = template
            .Replace("{z}", z.ToString())
            .Replace("{x}", x.ToString())
            .Replace("{y}", y.ToString());

        // The category carries the map name to match TileMapSource, so the desktop renderer and this
        // proxy share one on-disk tile cache -- which is what lets already-cached tiles resolve offline.
        var bytes = await _assets
            .GetAsync(url, Path.Combine("tiles", map.NormalizedName), ct)
            .ConfigureAwait(false);

        if (bytes is not { Length: > 0 })
            return TransparentPixel(context);

        context.Response.Headers["Cache-Control"] = ImageryCacheControl;
        return Results.Bytes(bytes, ContentTypeForTile(bytes));
    }

    /// <summary>
    /// Whitelists a <c>{mapId}</c> against the catalog's map keys, rejecting anything that is not a
    /// plain known key.
    /// </summary>
    /// <remarks>
    /// Route parameters never match a slash, so a traversal attempt cannot arrive as a real path; the
    /// dotted and encoded forms are rejected anyway, and an exact key compare means a case or spelling
    /// variant misses rather than silently resolving to some other map.
    /// </remarks>
    private GameMap? FindMap(string mapId)
    {
        if (string.IsNullOrEmpty(mapId)
            || mapId.Contains('/')
            || mapId.Contains('\\')
            || mapId.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var map in _catalog.Maps)
        {
            if (string.Equals(map.Key, mapId, StringComparison.Ordinal))
                return map;
        }

        return null;
    }

    /// <summary>Comma-separated floor names, trimmed and deduplicated. Absent or empty yields none.</summary>
    private static HashSet<string> ParseFloorNames(string? raw)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(raw))
            return names;

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            names.Add(part);

        return names;
    }

    /// <summary>The <c>base</c> flag: absent means on, and only an explicit "false" turns it off.</summary>
    private static bool ParseIncludeBase(string? raw) =>
        !string.Equals(raw?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The SVG response, with a short private cache.
    /// </summary>
    /// <remarks>
    /// The URL carries every parameter that changes the body, so a short private cache is safe and
    /// saves a re-request when the client toggles back to a floor combination it just used. The spec
    /// allows this or <c>no-store</c>; a few minutes is the more useful of the two and a shared cache
    /// is excluded by <c>private</c>.
    /// </remarks>
    private static IResult SvgResponse(string svg, HttpContext context)
    {
        context.Response.Headers["Cache-Control"] = ImageryCacheControl;
        return Results.Content(svg, "image/svg+xml; charset=utf-8");
    }

    /// <summary>
    /// A 1x1 transparent PNG, served in place of a missing tile so the browser console stays quiet.
    /// </summary>
    private static IResult TransparentPixel(HttpContext context)
    {
        context.Response.Headers["Cache-Control"] = ImageryCacheControl;
        return Results.Bytes(TransparentPixelPng, "image/png");
    }

    /// <summary>
    /// The content type for tile bytes: most pyramids are PNG but a few are WebP, so the magic bytes
    /// decide. Anything unrecognized is served as PNG, the common case.
    /// </summary>
    private static string ContentTypeForTile(byte[] bytes)
    {
        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "image/webp";
        }

        return "image/png";
    }

    /// <summary>
    /// A short private cache for imagery: the URL fully describes the body, and these files only
    /// change when tarkov.dev reworks a map. Private keeps it out of any shared or proxy cache.
    /// </summary>
    private const string ImageryCacheControl = "private, max-age=300";

    /// <summary>A 1x1 transparent PNG (70 bytes), the stand-in for a tile that cannot be served.</summary>
    private static readonly byte[] TransparentPixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    /// <summary>
    /// Works out the port the host actually bound, since a requested 0 means the OS picked one.
    /// </summary>
    /// <remarks>
    /// The address feature reports the bound address as <c>http://0.0.0.0:&lt;port&gt;</c>; the port
    /// is the part that matters. <c>app.Urls</c> is tried first and the feature as a fallback.
    /// </remarks>
    private static int ResolveBoundPort(WebApplication app, int requestedPort)
    {
        if (requestedPort != 0)
            return requestedPort;

        foreach (var url in app.Urls)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Port > 0)
                return uri.Port;
        }

        var feature = app.Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>();
        if (feature is not null)
        {
            foreach (var address in feature.Addresses)
            {
                if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
                    return uri.Port;
            }
        }

        return requestedPort;
    }
}
