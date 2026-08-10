using System.Net;
using System.Net.Sockets;
using TarkovMapCompanion.Diagnostics;
using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Party;

public enum PartyState
{
    Idle,
    Starting,
    Hosting,
    Joining,
    Joined,
    Failed,
}

/// <summary>One member of the squad as this machine currently understands them.</summary>
public sealed class PartyPeer
{
    public required string Name { get; init; }

    /// <summary>Normalized map name, or empty before their first screenshot.</summary>
    public required string Map { get; init; }

    public required GamePosition Position { get; init; }

    public required double Yaw { get; init; }

    /// <summary>True once a screenshot has actually placed them.</summary>
    public required bool HasPosition { get; init; }

    /// <summary>Whether this entry is us; the player overlay already draws that one.</summary>
    public bool IsSelf { get; init; }

    /// <summary>
    /// Their chosen marker color as <c>#RRGGBB</c>, or null when they have not picked one.
    /// </summary>
    /// <remarks>
    /// A string at this layer on purpose, so <c>Party</c> keeps its zero SkiaSharp dependency and
    /// the parsing happens where the drawing does.
    /// </remarks>
    public string? Color { get; init; }

    /// <summary>
    /// Round trip to the host in milliseconds, or null before it has been measured.
    /// </summary>
    /// <remarks>
    /// Always latency to the host, never to you. In a star topology there is no direct link
    /// between two guests, so there is nothing between them to time.
    /// </remarks>
    public int? LatencyMs { get; init; }

    /// <summary>Age at the moment the host sent it.</summary>
    public double AgeAtSend { get; init; }

    /// <summary>Local clock reading when it arrived, for ageing it further.</summary>
    public DateTime ReceivedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>How stale this position is right now.</summary>
    public double AgeSeconds => AgeAtSend + (DateTime.UtcNow - ReceivedAtUtc).TotalSeconds;
}

/// <summary>
/// A squad sharing positions, with one member acting as the hub.
/// </summary>
/// <remarks>
/// <para>
/// Star topology rather than a mesh: everyone dials the host, and the host fans out a complete
/// roster whenever anything changes. That means only one person has to be reachable from outside
/// their router, which is the difference between a feature that works for a squad and one that
/// needs every member to have configured their network.
/// </para>
/// <para>
/// Sending the whole roster on every change, instead of individual updates, is a deliberate
/// simplification. It is a few hundred bytes, and it makes joining halfway through a raid identical
/// to joining at the start -- you connect and immediately have everyone, rather than staring at a
/// blank map until each of them happens to take a screenshot.
/// </para>
/// <para>
/// Nothing here can break the rest of the app. Every failure path ends at <see cref="Leave"/>, and
/// the local map, trail, exits and markers never depend on any of it.
/// </para>
/// </remarks>
public sealed class PartySession : IDisposable
{
    /// <summary>Arbitrary, memorable, and outside the ranges anything common sits in.</summary>
    public const int DefaultPort = 24601;

    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _deadAfter;

    public PartySession()
        : this(PartyProtocol.HeartbeatInterval, PartyProtocol.DeadAfter)
    {
    }

    /// <summary>
    /// A session with the heartbeat wound faster, for tests.
    /// </summary>
    /// <remarks>
    /// The behavior worth pinning is that a connection which stops answering gets dropped, and at
    /// the shipping timings proving that costs twenty-one seconds of wall clock per test. The
    /// timings are the only thing a test needs to change, so they are the only thing exposed.
    /// </remarks>
    internal PartySession(TimeSpan heartbeatInterval, TimeSpan deadAfter)
    {
        _heartbeatInterval = heartbeatInterval;
        _deadAfter = deadAfter;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, PartyPeer> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HostedClient> _clients = [];

    /// <summary>Declared marker colors, by name. Held apart from peers so one survives the other.</summary>
    /// <remarks>
    /// A color arrives with Hello, before the first screenshot has produced a peer entry with a
    /// position. Keeping it on <see cref="PartyPeer"/> alone would lose it every time that record
    /// is rebuilt, which is on every update.
    /// </remarks>
    private readonly Dictionary<string, string?> _colors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every route the session knows about, by owner.</summary>
    private readonly Dictionary<string, PeerRoute> _routes = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cancellation;
    private TcpListener? _listener;
    private PortMapper? _mapper;
    private TcpClient? _upstream;
    private byte[]? _key;

    /// <summary>Serializes writes to the host, so two messages cannot interleave on the socket.</summary>
    private readonly SemaphoreSlim _upstreamWriteGate = new(1, 1);

    /// <summary>Proves the link is alive in both directions, and times the round trip.</summary>
    private Timer? _heartbeat;

    /// <summary>Guest side: when the host was last heard from, and the probe in flight.</summary>
    private DateTime _hostLastHeardUtc = DateTime.UtcNow;
    private long _hostPendingSeq;
    private long _hostPendingTicks;
    private long _heartbeatSeq;

    /// <summary>
    /// A short tag derived from the session secret, printed on every party log line.
    /// </summary>
    /// <remarks>
    /// The point of it is lining two machines' logs up side by side. Both ends of a session derive
    /// the same tag from the same secret without either of them sending it, so "these are the same
    /// session" is answerable from the logs alone -- and a mismatch immediately explains a squad
    /// that cannot see each other because somebody pasted an older code.
    /// </remarks>
    private string _fingerprint = "--------";

    /// <summary>"host" or "guest", fixed the moment a session starts.</summary>
    private string _role = "idle";

    private string _selfName = "Player";
    private string? _selfColor;
    private int _published;
    private PeerPosition? _selfPosition;
    private bool _disposed;

    /// <summary>
    /// Bumped every time a session ends. Reader loops capture it when they start and drop anything
    /// they were mid-way through applying once it has moved on.
    /// </summary>
    /// <remarks>
    /// Without this, leaving is a race it can lose. A frame that arrived just before the socket was
    /// torn down can finish being processed just after the roster was emptied, putting a peer back
    /// into a session that no longer exists -- and nothing would ever remove them again, so a
    /// teammate's marker would sit on the map until the app was restarted.
    /// </remarks>
    private int _generation;

    public PartyState State { get; private set; } = PartyState.Idle;

    /// <summary>The code to hand out. Only set while hosting.</summary>
    public string? Code { get; private set; }

    /// <summary>
    /// The port actually being listened on, and this machine's address on the LAN.
    /// </summary>
    /// <remarks>
    /// Reported rather than assumed. The preferred port can be taken, in which case the listener
    /// moves and any port forward pointed at the old number silently stops working -- so whatever
    /// the UI tells someone to forward has to be read back from the socket, not from a constant.
    /// </remarks>
    public int ListenPort { get; private set; }

    /// <summary>Null when it could not be determined.</summary>
    public string? LocalAddress { get; private set; }

    /// <summary>
    /// The address and port the squad actually dials, as encoded in the session code.
    /// </summary>
    /// <remarks>
    /// Not the same as <see cref="LocalAddress"/>, which is this machine on its own network and is
    /// only useful for typing into a router. This is what somebody else reaches you at.
    /// </remarks>
    public string? PublicEndpoint { get; private set; }

    /// <summary>False when the router refused, so the user has to forward the port themselves.</summary>
    public bool RouterOpenedPort { get; private set; }

    /// <summary>Our own name as the host knows it, which may be suffixed to avoid a clash.</summary>
    public string SelfName => _selfName;

    /// <summary>
    /// Guest side: round trip to the host, measured here. Null while hosting or before the first
    /// heartbeat comes back.
    /// </summary>
    public int? HostLatencyMs { get; private set; }

    /// <summary>The round trip the host last measured for a client, by name.</summary>
    private int? LatencyFor(string name)
    {
        lock (_gate)
            return _clients.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.LatencyMs;
    }

    /// <summary>
    /// Our own marker color as <c>#RRGGBB</c>, shared with the squad.
    /// </summary>
    /// <remarks>
    /// Assigning while a session is running announces it. Set before hosting or joining and it
    /// simply rides along on the Hello.
    /// </remarks>
    public string? SelfColor
    {
        get => _selfColor;
        set
        {
            if (string.Equals(_selfColor, value, StringComparison.OrdinalIgnoreCase))
                return;

            _selfColor = value;
            AnnounceColor();
        }
    }

    public bool IsActive => State is PartyState.Hosting or PartyState.Joined;

    /// <summary>Every route the session knows about, ours included.</summary>
    public IReadOnlyList<PeerRoute> Routes
    {
        get { lock (_gate) return _routes.Values.ToArray(); }
    }

    /// <summary>Raised whenever the roster or the connection state changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised when somebody's route changes. Separate from <see cref="Changed"/> on purpose: a
    /// position arrives every few seconds and rebuilding routes on each one would be pure waste.
    /// </summary>
    public event EventHandler? RoutesChanged;

    /// <summary>Human-readable progress and problems.</summary>
    public event EventHandler<string>? Status;

    /// <summary>Raised for every ping, including our own, so one code path draws them all.</summary>
    public event EventHandler<PeerPosition>? PingReceived;

    /// <summary>
    /// Marks a spot for the squad.
    /// </summary>
    /// <remarks>
    /// Shown locally whether or not a session is running. Alone, it is a scratch mark that clears
    /// itself; in a session it is also passed to everyone else. Doing nothing when solo would be a
    /// click that silently accomplishes nothing.
    /// </remarks>
    public void SendPing(string map, GamePosition position)
    {
        var ping = new PeerPosition
        {
            Name = _selfName,
            Map = map,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
        };

        LogParty($"ping placed at {position.X:F0},{position.Z:F0} on {map}");

        PingReceived?.Invoke(this, ping);

        if (!IsActive)
            return;

        if (State == PartyState.Hosting)
        {
            _ = RelayPingAsync(ping, from: null);
            return;
        }

        Send(new PartyMessage { Kind = PartyMessageKind.Ping, Position = ping });
    }

    /// <summary>Passes a ping to every connected peer except whoever sent it.</summary>
    private async Task RelayPingAsync(PeerPosition ping, HostedClient? from)
    {
        HostedClient[] clients;
        lock (_gate)
            clients = _clients.ToArray();

        if (_key is not { } key)
            return;

        var message = new PartyMessage { Kind = PartyMessageKind.Ping, Position = ping };

        foreach (var client in clients)
        {
            if (ReferenceEquals(client, from))
                continue;

            try
            {
                await PartyProtocol.WriteAsync(client.Stream, key, message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"could not send a ping to {client.Name}: {ex.Message}");
            }
        }
    }

    public IReadOnlyList<PartyPeer> Peers
    {
        get { lock (_gate) return _peers.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    // ---- Hosting ------------------------------------------------------------

    /// <param name="useRouter">
    /// False hosts on loopback and skips UPnP entirely, which is how the tests and the
    /// <c>--party-test</c> harness exercise the real sockets and the real protocol without needing
    /// a router, a public address, or a second machine.
    /// </param>
    /// <param name="port">
    /// The port to listen on. Zero lets the OS pick a free one, which is right when nothing depends
    /// on the number staying the same -- tests, and the loopback harness.
    /// </param>
    public async Task<bool> HostAsync(
        string displayName,
        int port = 0,
        CancellationToken cancellationToken = default,
        bool useRouter = true)
    {
        Leave();

        _selfName = PartyProtocol.CleanName(displayName);
        State = PartyState.Starting;

        // Leave() cleared the color table, and the host's own entry is never filled in by a Hello
        // the way a guest's is, so it has to be put back here or the host is the one person in the
        // squad drawn from the fallback palette.
        lock (_gate)
            _colors[_selfName] = _selfColor;

        Raise();

        var secret = SessionCode.NewSecret();
        _key = PartyProtocol.DeriveKey(secret);
        _fingerprint = Fingerprint(secret);
        _role = "host";
        _cancellation = new CancellationTokenSource();

        LogParty($"starting as \"{_selfName}\", requested port {port}");

        try
        {
            port = StartListener(port);

            ListenPort = port;
            LocalAddress = PortMapper.LocalAddress()?.ToString();
            RouterOpenedPort = false;

            PortMapping? mapping;

            if (useRouter)
            {
                Status?.Invoke(this, "Asking your router to open a port...");

                _mapper = new PortMapper();
                mapping = await _mapper.MapAsync(port, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                mapping = new PortMapping(IPAddress.Loopback, port, Mapped: true);
            }

            if (mapping is null)
            {
                LogParty("WARN no usable address; UPnP failed and no public address was found");

                Leave();
                State = PartyState.Failed;
                Status?.Invoke(
                    this,
                    $"Could not open a port automatically. Forward TCP {port} to this PC and try again, "
                    + "or have someone else host.");

                Raise();
                return false;
            }

            Code = SessionCode.Format(mapping.ExternalAddress, mapping.Port, secret);
            PublicEndpoint = $"{mapping.ExternalAddress}:{mapping.Port}";
            RouterOpenedPort = mapping.Mapped;
            State = PartyState.Hosting;

            StartHeartbeat();

            LogParty(
                $"listening on {LocalAddress}:{ListenPort}, external {Mask(mapping.ExternalAddress)}:{mapping.Port}, "
                + $"router opened it: {mapping.Mapped}");

            UpdateSelf();
            _ = Task.Run(() => AcceptLoopAsync(_cancellation.Token), CancellationToken.None);

            Status?.Invoke(this, mapping.Mapped
                ? "Session open. Share the code with your squad."
                : $"Session open, but your router did not open the port itself. If nobody can join, "
                  + $"forward TCP {mapping.Port} to this PC, or have someone else host.");

            Raise();
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            Leave();
            State = PartyState.Failed;

            Status?.Invoke(
                this,
                $"Port {port} is already in use. Another copy of the app is probably hosting; "
                + "close it, or pick a different party port in Settings.");

            Raise();
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("could not start hosting", ex);
            Leave();
            State = PartyState.Failed;
            Status?.Invoke(this, $"Could not start the session: {ex.Message}");
            Raise();
            return false;
        }
    }

    /// <summary>
    /// Opens the listener on exactly <paramref name="port"/>, or any free port when it is zero.
    /// </summary>
    /// <remarks>
    /// It used to fall back to a free port when the requested one was taken. That is fine when the
    /// router opens the port for us, since the code carries whatever we ended up with -- but it
    /// quietly destroys a manual port forward, which is pinned to one number. The symptom is a
    /// session that looks perfectly healthy and that nobody can join, with nothing on screen
    /// suggesting why. Better to refuse and say so.
    /// </remarks>
    private int StartListener(int port)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();

        return ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { } listener)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => ServeAsync(client, cancellationToken), CancellationToken.None);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"party accept failed: {ex.Message}");
            }
        }
    }

    /// <summary>Serves one joined peer for as long as it stays connected.</summary>
    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        HostedClient? hosted = null;
        var generation = CurrentGeneration;

        try
        {
            client.NoDelay = true;

            var stream = client.GetStream();
            var key = _key ?? throw new InvalidOperationException("hosting without a key");

            // The first frame has to decrypt, which is the whole of authentication: holding the
            // secret from the code is what it means to be invited.
            var hello = await PartyProtocol.ReadAsync(stream, key, cancellationToken).ConfigureAwait(false);
            if (hello is not { Kind: PartyMessageKind.Hello })
                return;

            var name = UniqueName(PartyProtocol.CleanName(hello.Name));
            hosted = new HostedClient(client, stream, name);

            lock (_gate)
            {
                _clients.Add(hosted);
                _colors[name] = hello.Color;
            }

            LogParty(
                $"\"{name}\" joined from {Mask(((IPEndPoint)client.Client.RemoteEndPoint!).Address)}, "
                + $"speaking v{hello.Version}");

            Status?.Invoke(this, $"{name} joined.");

            SetPeer(name, null, isSelf: false, generation);
            await BroadcastAsync(cancellationToken).ConfigureAwait(false);
            await BroadcastRoutesAsync(cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await PartyProtocol.ReadAsync(stream, key, cancellationToken).ConfigureAwait(false);
                if (message is null)
                    break;

                // Any frame at all is proof of life, not just a heartbeat reply. A squad that is
                // moving refreshes this constantly and never comes near the timeout.
                lock (_gate)
                    hosted.LastHeardUtc = DateTime.UtcNow;

                if (message.Kind == PartyMessageKind.Heartbeat)
                {
                    await SendToAsync(
                        hosted,
                        new PartyMessage { Kind = PartyMessageKind.HeartbeatAck, Seq = message.Seq },
                        cancellationToken).ConfigureAwait(false);

                    continue;
                }

                if (message.Kind == PartyMessageKind.HeartbeatAck)
                {
                    int? measured = null;

                    lock (_gate)
                    {
                        if (message.Seq == hosted.PendingSeq)
                            measured = hosted.LatencyMs = ElapsedMs(hosted.PendingSentTicks);
                    }

                    if (measured is { } ms)
                        NoteLatency(name, ms);

                    // Not broadcast on its own: the roster carries latency and goes out constantly
                    // anyway, and a fan-out every five seconds per client for a number that moves
                    // by a millisecond is not worth the frames.
                    continue;
                }

                if (message.Kind == PartyMessageKind.Ping && message.Position is { } ping)
                {
                    // Named from the connection, not the payload, so nobody can ping as somebody
                    // else.
                    ping.Name = name;

                    LogParty($"ping from \"{name}\" at {ping.X:F0},{ping.Z:F0} on {ping.Map}");
                    PingReceived?.Invoke(this, ping);
                    await RelayPingAsync(ping, hosted).ConfigureAwait(false);
                    continue;
                }

                if (message.Kind == PartyMessageKind.Color)
                {
                    // Applied to the connection's name, never the payload's, for the same reason
                    // pings are: otherwise anybody could recolor anybody.
                    Recolor(name, message.Color);
                    await BroadcastAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (message.Kind == PartyMessageKind.Route)
                {
                    var route = message.Route ?? new PeerRoute();
                    route.Name = name;

                    StoreRoute(route, generation);
                    await BroadcastRoutesAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (message.Kind != PartyMessageKind.Position || message.Position is null)
                    continue;

                // A position carries the sender's color too, so a color set before anybody was
                // listening still arrives without needing its own message.
                if (message.Position.Color is { } declared)
                    Recolor(name, declared);

                SetPeer(name, message.Position, isSelf: false, generation);
                await BroadcastAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // A peer closing, or the session ending. Both are ordinary.
        }
        catch (Exception ex)
        {
            // Includes a frame that would not authenticate, which is what a wrong code looks like:
            // the type is the useful part, so log it rather than only the message.
            Log.Warn($"[party {_fingerprint} host] peer dropped ({hosted?.Name ?? "before hello"}): "
                     + $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (hosted is not null)
            {
                lock (_gate)
                {
                    _clients.Remove(hosted);
                    _peers.Remove(hosted.Name);
                    _colors.Remove(hosted.Name);
                    _routes.Remove(hosted.Name);
                }

                LogParty($"\"{hosted.Name}\" left; {_clients.Count} still connected");
                Status?.Invoke(this, $"{hosted.Name} left.");
                hosted.Dispose();

                await BroadcastAsync(CancellationToken.None).ConfigureAwait(false);
                await BroadcastRoutesAsync(CancellationToken.None).ConfigureAwait(false);
            }

            client.Dispose();
        }
    }

    /// <summary>
    /// Starts the heartbeat. Both roles run one; only what it does differs.
    /// </summary>
    private void StartHeartbeat()
    {
        _heartbeat?.Dispose();

        _hostLastHeardUtc = DateTime.UtcNow;

        _heartbeat = new Timer(_ => Beat(), null, _heartbeatInterval, _heartbeatInterval);
    }

    /// <summary>
    /// One round of "still there?", and hanging up on whoever has stopped answering.
    /// </summary>
    /// <remarks>
    /// A timer callback, so nothing may escape it: an exception here would come out on a thread
    /// pool thread with no handler and take the process down.
    /// </remarks>
    private void Beat()
    {
        try
        {
            if (State == PartyState.Hosting)
                BeatAsHost();
            else if (State == PartyState.Joined)
                BeatAsGuest();
        }
        catch (Exception ex)
        {
            Log.Error("party heartbeat failed", ex);
        }
    }

    private void BeatAsHost()
    {
        HostedClient[] clients;
        lock (_gate)
            clients = _clients.ToArray();

        var now = DateTime.UtcNow;

        foreach (var client in clients)
        {
            DateTime lastHeard;
            lock (_gate)
                lastHeard = client.LastHeardUtc;

            if (now - lastHeard > _deadAfter)
            {
                // Closing the socket is what makes their serve loop return and clean up. Nothing
                // else here touches the roster.
                LogParty($"\"{client.Name}\" has not been heard from in {(now - lastHeard).TotalSeconds:F0}s; dropping");
                Status?.Invoke(this, $"Lost contact with {client.Name}.");
                Disconnect(client);
                continue;
            }

            var seq = Interlocked.Increment(ref _heartbeatSeq);

            lock (_gate)
            {
                client.PendingSeq = seq;
                client.PendingSentTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            _ = SendToAsync(
                client,
                new PartyMessage { Kind = PartyMessageKind.Heartbeat, Seq = seq },
                CancellationToken.None);
        }
    }

    private void BeatAsGuest()
    {
        if (DateTime.UtcNow - _hostLastHeardUtc > _deadAfter)
        {
            // The failure this exists for: the socket is open, the read is blocked, and nothing has
            // arrived for twenty seconds. Saying so beats sitting there looking connected.
            LogParty("the host has gone quiet; leaving the session");
            Status?.Invoke(this, "Lost contact with the host. The session has ended.");
            Leave();
            return;
        }

        var seq = Interlocked.Increment(ref _heartbeatSeq);

        _hostPendingSeq = seq;
        _hostPendingTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        Send(new PartyMessage { Kind = PartyMessageKind.Heartbeat, Seq = seq });
    }

    /// <summary>Turns a stopwatch reading taken when a probe went out into a round trip.</summary>
    private static int ElapsedMs(long sinceTicks) =>
        (int)Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(sinceTicks).TotalMilliseconds);

    /// <summary>
    /// Sends one message to one client, and hangs up on them if it cannot be sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hanging up is the important half. This used to log the failure and carry on with the client
    /// still in the roster and its socket still open, which is the worst of both worlds: the host
    /// believes the squad is intact, the guest's read blocks forever on a connection that is never
    /// going to carry anything again, and the only cure anybody found was rejoining. Closing the
    /// socket unblocks that read, so the guest finds out too.
    /// </para>
    /// <para>
    /// Returns whether it got through, so a caller mid-broadcast can keep going to the others.
    /// </para>
    /// </remarks>
    private async Task<bool> SendToAsync(HostedClient client, PartyMessage message, CancellationToken cancellationToken)
    {
        if (_key is not { } key)
            return false;

        try
        {
            await client.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }

        try
        {
            await PartyProtocol.WriteAsync(client.Stream, key, message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            LogParty($"dropping \"{client.Name}\": {ex.GetType().Name}: {ex.Message}");
            Disconnect(client);
            return false;
        }
        finally
        {
            try { client.WriteGate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Closes a client's socket, which is how the rest of the code finds out they are gone.
    /// </summary>
    /// <remarks>
    /// Deliberately does not touch the roster. Disposing the stream makes the blocking read in that
    /// client's own serve loop return, and its finally block is the one place membership is
    /// removed and the change announced. Two paths doing that would race.
    /// </remarks>
    private static void Disconnect(HostedClient client)
    {
        try { client.Stream.Dispose(); } catch (ObjectDisposedException) { }
        try { client.Client.Dispose(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Sends the full roster to everyone, each addressed with the name we know them by.</summary>
    private async Task BroadcastAsync(CancellationToken cancellationToken)
    {
        Raise();

        HostedClient[] clients;
        lock (_gate)
            clients = _clients.ToArray();

        if (clients.Length == 0 || _key is not { } key)
            return;

        var roster = SnapshotRoster();

        foreach (var client in clients)
        {
            var message = new PartyMessage
            {
                Kind = PartyMessageKind.Roster,
                Name = client.Name,
                Roster = roster,
            };

            await SendToAsync(client, message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends every route the host knows to everyone, self included.
    /// </summary>
    /// <remarks>
    /// The whole set each time, like the roster, so a client that missed one self-corrects on the
    /// next and a late joiner needs no special path. Receivers filter their own back out.
    /// </remarks>
    private async Task BroadcastRoutesAsync(CancellationToken cancellationToken)
    {
        RaiseRoutes();

        HostedClient[] clients;
        lock (_gate)
            clients = _clients.ToArray();

        if (clients.Length == 0 || _key is not { } key)
            return;

        var routes = SnapshotRoutes();

        foreach (var client in clients)
        {
            var message = new PartyMessage { Kind = PartyMessageKind.Routes, Routes = routes };
            await SendToAsync(client, message, cancellationToken).ConfigureAwait(false);
        }
    }

    private List<PeerRoute> SnapshotRoutes()
    {
        lock (_gate)
            return _routes.Values.Take(PartyProtocol.MaxSharedRoutes).ToList();
    }

    /// <summary>Records one player's route, truncating rather than letting a frame grow unbounded.</summary>
    private void StoreRoute(PeerRoute route, int generation)
    {
        if (route.Points.Count > PartyProtocol.MaxRoutePoints)
        {
            Log.Warn($"truncating {route.Name}'s route from {route.Points.Count} points");
            route.Points = route.Points.Take(PartyProtocol.MaxRoutePoints).ToList();
        }

        lock (_gate)
        {
            if (generation != _generation)
                return;

            // An empty route is a real message, not an absence: it is how clearing your markers
            // reaches everybody. Dropping it would leave a phantom route on every other map until
            // the session ended.
            if (route.Points.Count == 0)
                _routes.Remove(route.Name);
            else
                _routes[route.Name] = route;
        }
    }

    private List<PeerPosition> SnapshotRoster()
    {
        lock (_gate)
        {
            return _peers.Values.Select(p => new PeerPosition
            {
                Name = p.Name,
                Map = p.Map,
                Color = _colors.GetValueOrDefault(p.Name),
                X = p.Position.X,
                Y = p.Position.Y,
                Z = p.Position.Z,
                Yaw = p.Yaw,
                LatencyMs = LatencyFor(p.Name),

                // Aged on this machine's clock only, so nobody has to trust anybody else's.
                AgeSeconds = p.HasPosition ? p.AgeSeconds : -1,
            }).ToList();
        }
    }

    private string UniqueName(string wanted)
    {
        lock (_gate)
        {
            if (!_peers.ContainsKey(wanted))
                return wanted;

            for (var suffix = 2; suffix < 100; suffix++)
            {
                var candidate = $"{wanted} {suffix}";
                if (!_peers.ContainsKey(candidate))
                    return candidate;
            }

            return $"{wanted} {Guid.NewGuid():N}"[..16];
        }
    }

    // ---- Joining ------------------------------------------------------------

    public async Task<bool> JoinAsync(
        string code,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        Leave();

        if (!SessionCode.TryParse(code, out var endPoint, out var secret))
        {
            State = PartyState.Failed;
            Status?.Invoke(this, "That does not look like a session code.");
            Raise();
            return false;
        }

        _selfName = PartyProtocol.CleanName(displayName);
        _key = PartyProtocol.DeriveKey(secret);
        _fingerprint = Fingerprint(secret);
        _role = "guest";
        _cancellation = new CancellationTokenSource();

        State = PartyState.Joining;
        Raise();

        LogParty($"connecting to {Mask(endPoint.Address)}:{endPoint.Port} as \"{_selfName}\"");

        try
        {
            _upstream = new TcpClient { NoDelay = true };

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await _upstream.ConnectAsync(endPoint, timeout.Token).ConfigureAwait(false);
            }

            var stream = _upstream.GetStream();

            await PartyProtocol.WriteAsync(
                stream,
                _key,
                new PartyMessage { Kind = PartyMessageKind.Hello, Name = _selfName, Color = _selfColor },
                cancellationToken).ConfigureAwait(false);

            State = PartyState.Joined;
            LogParty("connected, sent hello");

            Status?.Invoke(this, "Connected to the session.");
            Raise();

            _ = Task.Run(() => ReceiveLoopAsync(stream, _cancellation.Token), CancellationToken.None);

            StartHeartbeat();

            // Anything already known locally goes up straight away, so the squad sees us without
            // waiting for the next screenshot.
            if (_selfPosition is not null)
                SendSelf();

            return true;
        }
        catch (Exception ex)
        {
            LogParty($"WARN could not join: {ex.GetType().Name}: {ex.Message}");
            Leave();

            State = PartyState.Failed;
            Status?.Invoke(this, ex is OperationCanceledException
                ? "No answer from the host. Check the code, and that they are still hosting."
                : $"Could not join: {ex.Message}");

            Raise();
            return false;
        }
    }

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var generation = CurrentGeneration;
        var lastRosterSize = -1;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await PartyProtocol
                    .ReadAsync(stream, _key!, cancellationToken)
                    .ConfigureAwait(false);

                if (message is null)
                    break;

                _hostLastHeardUtc = DateTime.UtcNow;

                if (message.Kind == PartyMessageKind.Heartbeat)
                {
                    Send(new PartyMessage { Kind = PartyMessageKind.HeartbeatAck, Seq = message.Seq });
                    continue;
                }

                if (message.Kind == PartyMessageKind.HeartbeatAck)
                {
                    if (message.Seq == _hostPendingSeq)
                    {
                        HostLatencyMs = ElapsedMs(_hostPendingTicks);
                        Raise();
                    }

                    continue;
                }

                if (message.Kind == PartyMessageKind.Ping && message.Position is { } ping)
                {
                    LogParty($"ping from \"{ping.Name}\" at {ping.X:F0},{ping.Z:F0} on {ping.Map}");
                    PingReceived?.Invoke(this, ping);
                    continue;
                }

                if (message.Kind == PartyMessageKind.Routes)
                {
                    ApplyRoutes(message.Routes ?? [], generation);
                    RaiseRoutes();
                    continue;
                }

                // Anything a later build invented. Skipped rather than fatal, which is the whole
                // point of the tolerant kind converter.
                if (message.Kind == PartyMessageKind.Unknown)
                {
                    LogParty("ignoring a message kind this build does not know");
                    continue;
                }

                if (message.Kind != PartyMessageKind.Roster || message.Roster is null)
                    continue;

                // The host is the authority on names, including ours if it had to disambiguate.
                if (!string.IsNullOrEmpty(message.Name))
                    _selfName = message.Name;

                // Only when the membership changes: a roster arrives on every position anyone
                // publishes, and logging all of them would bury everything else.
                if (message.Roster.Count != lastRosterSize)
                {
                    lastRosterSize = message.Roster.Count;

                    LogParty(
                        $"roster now {lastRosterSize}: "
                        + string.Join(", ", message.Roster.Select(r =>
                            $"{r.Name}{(r.AgeSeconds < 0 ? " (no position)" : $" on {r.Map}")}")));
                }

                ApplyRoster(message.Roster, generation);
                Raise();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Host closed, or we left.
        }
        catch (Exception ex)
        {
            Log.Warn($"party connection lost: {ex.Message}");
        }
        finally
        {
            if (State == PartyState.Joined)
            {
                Status?.Invoke(this, "The host closed the session.");
                Leave();
            }
        }
    }

    internal void ApplyRoster(List<PeerPosition> roster, int generation)
    {
        lock (_gate)
        {
            if (generation != _generation)
                return;

            _peers.Clear();

            foreach (var entry in roster)
            {
                _peers[entry.Name] = new PartyPeer
                {
                    Name = entry.Name,
                    Map = entry.Map,
                    Position = new GamePosition(entry.X, entry.Y, entry.Z),
                    Yaw = entry.Yaw,
                    HasPosition = entry.AgeSeconds >= 0,
                    AgeAtSend = Math.Max(entry.AgeSeconds, 0),
                    Color = entry.Color,
                    LatencyMs = entry.LatencyMs,
                    IsSelf = string.Equals(entry.Name, _selfName, StringComparison.OrdinalIgnoreCase),
                };
            }
        }
    }

    /// <summary>Replaces every known route with what the host just sent.</summary>
    internal void ApplyRoutes(List<PeerRoute> routes, int generation)
    {
        lock (_gate)
        {
            if (generation != _generation)
                return;

            _routes.Clear();

            foreach (var route in routes.Take(PartyProtocol.MaxSharedRoutes))
            {
                if (route.Points.Count > PartyProtocol.MaxRoutePoints)
                    route.Points = route.Points.Take(PartyProtocol.MaxRoutePoints).ToList();

                if (route.Points.Count > 0)
                    _routes[route.Name] = route;
            }
        }
    }

    /// <summary>
    /// Shares our own route, replacing whatever the squad last heard from us.
    /// </summary>
    /// <remarks>
    /// An empty list is a real message and has to be sent: it is how clearing your markers, or
    /// walking the last one off, reaches everybody else. Treating "nothing to say" as "say nothing"
    /// would leave a phantom route on every teammate's map until the session ended.
    /// </remarks>
    public void PublishRoute(string map, IReadOnlyList<(double X, double Z)> points)
    {
        var route = new PeerRoute
        {
            Name = _selfName,
            Map = map,
            Points = points
                .Take(PartyProtocol.MaxRoutePoints)
                .Select(p => new RoutePoint { X = p.X, Z = p.Z })
                .ToList(),
        };

        if (!IsActive)
            return;

        if (_role == "host")
        {
            StoreRoute(route, CurrentGeneration);
            _ = BroadcastRoutesAsync(_cancellation?.Token ?? CancellationToken.None);
            return;
        }

        Send(new PartyMessage { Kind = PartyMessageKind.Route, Route = route });
    }

    /// <summary>
    /// Records somebody's color, and rebuilds their roster entry to match.
    /// </summary>
    /// <remarks>
    /// Both halves are needed. The table is what the fan-out reads, but the host's own view of the
    /// squad comes from the peer records, which are only rebuilt when a position arrives -- so
    /// without this a color change would be visible to everybody except the person hosting, until
    /// the next screenshot happened to land.
    /// </remarks>
    private void Recolor(string name, string? color)
    {
        lock (_gate)
        {
            _colors[name] = color;

            if (!_peers.TryGetValue(name, out var peer))
                return;

            _peers[name] = new PartyPeer
            {
                Name = peer.Name,
                Map = peer.Map,
                Position = peer.Position,
                Yaw = peer.Yaw,
                HasPosition = peer.HasPosition,
                IsSelf = peer.IsSelf,
                AgeAtSend = peer.AgeAtSend,

                // Carried over rather than reset, or recoloring somebody would make their position
                // look freshly reported when nothing about it has changed.
                ReceivedAtUtc = peer.ReceivedAtUtc,
                LatencyMs = peer.LatencyMs,
                Color = color,
            };
        }
    }

    /// <summary>
    /// Records a freshly measured round trip against a peer.
    /// </summary>
    /// <remarks>
    /// The record has to be rebuilt rather than the number simply stored elsewhere. The roster the
    /// host broadcasts reads latency live, so guests saw it update while the host's own panel kept
    /// whatever was true when that peer last moved -- which for somebody standing still is "never
    /// measured".
    /// </remarks>
    private void NoteLatency(string name, int latencyMs)
    {
        lock (_gate)
        {
            if (!_peers.TryGetValue(name, out var peer) || peer.LatencyMs == latencyMs)
                return;

            _peers[name] = new PartyPeer
            {
                Name = peer.Name,
                Map = peer.Map,
                Position = peer.Position,
                Yaw = peer.Yaw,
                HasPosition = peer.HasPosition,
                IsSelf = peer.IsSelf,
                AgeAtSend = peer.AgeAtSend,
                ReceivedAtUtc = peer.ReceivedAtUtc,
                Color = peer.Color,
                LatencyMs = latencyMs,
            };
        }

        Raise();
    }

    /// <summary>Tells the squad our color changed, when there is no screenshot due to carry it.</summary>
    private void AnnounceColor()
    {
        if (!IsActive)
            return;

        if (_role == "host")
        {
            Recolor(_selfName, _selfColor);
            _ = BroadcastAsync(_cancellation?.Token ?? CancellationToken.None);
            return;
        }

        Send(new PartyMessage { Kind = PartyMessageKind.Color, Color = _selfColor });
    }

    // ---- Position sharing ---------------------------------------------------

    /// <summary>
    /// Publishes our own position. Safe to call from the folder-watcher thread; the send happens
    /// off it, and a failure never propagates back into reading screenshots.
    /// </summary>
    public void Publish(string map, GamePosition position, double yaw)
    {
        _selfPosition = new PeerPosition
        {
            Name = _selfName,
            Map = map,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
            Yaw = yaw,

            // Riding along here means a color set before anybody was listening still lands, without
            // needing its own message on every join.
            Color = _selfColor,
        };

        if (!IsActive)
            return;

        // Positions arrive every screenshot; logging each would drown the file. A periodic count
        // is enough to answer "is this end publishing at all", which is the only question the log
        // needs to settle -- and it is exactly the question a squad member who cannot be seen
        // needs answered.
        if (++_published % 10 == 1)
            LogParty($"published {_published} position(s), latest on {map}");

        if (State == PartyState.Hosting)
        {
            UpdateSelf();
            _ = BroadcastAsync(_cancellation?.Token ?? CancellationToken.None);
            return;
        }

        SendSelf();
    }

    private void SendSelf()
    {
        if (_selfPosition is null)
            return;

        Send(new PartyMessage { Kind = PartyMessageKind.Position, Position = _selfPosition });
    }

    /// <summary>Sends to the host, off the calling thread and without letting a failure escape.</summary>
    /// <remarks>
    /// One writer at a time, for the same reason the host serializes its own sends: a position, a
    /// route and a heartbeat can all be dispatched within a millisecond of each other, and two of
    /// them interleaving on the socket leaves the stream permanently misframed.
    /// </remarks>
    private void Send(PartyMessage message)
    {
        if (_upstream?.Connected != true || _key is not { } key)
            return;

        var stream = _upstream.GetStream();

        _ = Task.Run(async () =>
        {
            var token = _cancellation?.Token ?? CancellationToken.None;

            try
            {
                await _upstreamWriteGate.WaitAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                await PartyProtocol.WriteAsync(stream, key, message, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"could not send {message.Kind} to the host: {ex.Message}");
            }
            finally
            {
                try { _upstreamWriteGate.Release(); } catch (ObjectDisposedException) { }
            }
        });
    }

    /// <summary>
    /// Which session is current. Internal so a test can hold onto one, end the session, and then
    /// apply an update from it -- the race itself is far too narrow to reproduce by timing.
    /// </summary>
    internal int CurrentGeneration
    {
        get { lock (_gate) return _generation; }
    }

    /// <summary>Test seams for writing a frame by hand, to check the host does not trust it.</summary>
    internal NetworkStream? UpstreamStreamForTests => _upstream?.GetStream();

    /// <inheritdoc cref="UpstreamStreamForTests"/>
    internal byte[]? KeyForTests => _key;

    private void UpdateSelf() => SetPeer(_selfName, _selfPosition, isSelf: true, CurrentGeneration);

    /// <param name="generation">
    /// The session this update belongs to. An update from a session that has since ended is
    /// discarded rather than resurrecting a peer nobody is connected to any more.
    /// </param>
    private void SetPeer(string name, PeerPosition? position, bool isSelf, int generation)
    {
        lock (_gate)
        {
            if (generation != _generation)
                return;

            _peers[name] = new PartyPeer
            {
                Name = name,
                Map = position?.Map ?? "",
                Position = position is null
                    ? new GamePosition(0, 0, 0)
                    : new GamePosition(position.X, position.Y, position.Z),
                Yaw = position?.Yaw ?? 0,
                HasPosition = position is not null,
                IsSelf = isSelf,
                AgeAtSend = 0,
                LatencyMs = isSelf ? null : LatencyFor(name),

                // Read back from the color table rather than from the position, because a color
                // arrives with the Hello and this record is rebuilt on every update. Taking it from
                // the position alone would blank it every time somebody moved, and leave the host
                // the one machine in the squad that could not see its own squad's colors.
                Color = _colors.GetValueOrDefault(name),
            };
        }
    }

    // ---- Teardown -----------------------------------------------------------

    /// <summary>
    /// Ends whatever is running and returns to idle, ready to start again.
    /// </summary>
    /// <remarks>
    /// Deliberately safe to call at any time, including when nothing is running. Restarting a
    /// session is the first thing anyone tries when something is wrong, so it has to be reliable
    /// rather than a path that only works from the states somebody thought of.
    /// </remarks>
    public void Leave()
    {
        if (IsActive)
            LogParty($"session ended after publishing {_published} position(s)");

        // Before anything else. A heartbeat firing mid-teardown would find a half-dismantled
        // session and try to hang up on clients that are already gone.
        _heartbeat?.Dispose();
        _heartbeat = null;
        HostLatencyMs = null;

        HostedClient[] clients;

        lock (_gate)
        {
            // Invalidate first, so anything still in flight is already too late to be applied.
            _generation++;

            clients = _clients.ToArray();
            _clients.Clear();
            _peers.Clear();
        }

        try { _cancellation?.Cancel(); } catch (ObjectDisposedException) { }

        foreach (var client in clients)
            client.Dispose();

        try { _listener?.Stop(); } catch (SocketException) { }

        _upstream?.Dispose();
        _mapper?.Dispose();
        _cancellation?.Dispose();

        _listener = null;
        _upstream = null;
        _mapper = null;
        _cancellation = null;
        _key = null;
        _published = 0;
        Code = null;
        PublicEndpoint = null;

        // Our own last position is session state too. Kept, it would be republished to whoever we
        // connect to next, before a screenshot has said we are anywhere.
        _selfPosition = null;

        // Colors and routes are session state as much as the roster is. A route left behind would
        // be republished to whoever we connect to next, before anybody has drawn a marker.
        lock (_gate)
        {
            _colors.Clear();
            _routes.Clear();
        }

        RaiseRoutes();

        if (State != PartyState.Failed)
            State = PartyState.Idle;

        Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    private void RaiseRoutes() => RoutesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Every party line carries the session tag and which side of it we are.
    /// </summary>
    /// <remarks>
    /// The role is recorded rather than read back from <see cref="State"/>. Inferring it meant the
    /// very first line a host writes -- logged while the state is still Starting -- called itself a
    /// guest, which is a lie in precisely the file someone reaches for when the two ends disagree.
    /// </remarks>
    private void LogParty(string message) => Log.Info($"[party {_fingerprint} {_role}] {message}");

    /// <summary>
    /// Identifies a session without revealing the code that would let somebody join it.
    /// </summary>
    private static string Fingerprint(ReadOnlySpan<byte> secret) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(secret))[..8].ToLowerInvariant();

    /// <summary>
    /// Keeps the last octet out of the log, so a pasted log does not hand out somebody's address.
    /// </summary>
    private static string Mask(IPAddress address)
    {
        var text = address.ToString();
        var lastDot = text.LastIndexOf('.');

        return lastDot < 0 ? "?" : $"{text[..lastDot]}.x";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Leave();
    }

    private sealed record HostedClient(TcpClient Client, NetworkStream Stream, string Name) : IDisposable
    {
        /// <summary>
        /// One writer at a time on this socket.
        /// </summary>
        /// <remarks>
        /// Every client's read loop broadcasts to every other client, so with three people moving
        /// there are routinely several tasks writing to the same stream at once. Nothing stopped
        /// them interleaving, and half of one frame followed by half of another is not a frame:
        /// the length prefix no longer lines up and the connection never decodes anything again.
        /// </remarks>
        public SemaphoreSlim WriteGate { get; } = new(1, 1);

        /// <summary>When anything at all last arrived from this client.</summary>
        public DateTime LastHeardUtc { get; set; } = DateTime.UtcNow;

        /// <summary>The heartbeat we are waiting on, and when it went out.</summary>
        public long PendingSeq { get; set; }

        public long PendingSentTicks { get; set; }

        /// <summary>Last measured round trip, or null before the first reply.</summary>
        public int? LatencyMs { get; set; }

        public void Dispose()
        {
            try { Stream.Dispose(); } catch (ObjectDisposedException) { }
            try { Client.Dispose(); } catch (ObjectDisposedException) { }

            WriteGate.Dispose();
        }
    }
}
