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

    private readonly object _gate = new();
    private readonly Dictionary<string, PartyPeer> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HostedClient> _clients = [];

    private CancellationTokenSource? _cancellation;
    private TcpListener? _listener;
    private PortMapper? _mapper;
    private TcpClient? _upstream;
    private byte[]? _key;

    private string _selfName = "Player";
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

    /// <summary>False when the router refused, so the user has to forward the port themselves.</summary>
    public bool RouterOpenedPort { get; private set; }

    /// <summary>Our own name as the host knows it, which may be suffixed to avoid a clash.</summary>
    public string SelfName => _selfName;

    public bool IsActive => State is PartyState.Hosting or PartyState.Joined;

    /// <summary>Raised whenever the roster or the connection state changes.</summary>
    public event EventHandler? Changed;

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
        Raise();

        var secret = SessionCode.NewSecret();
        _key = PartyProtocol.DeriveKey(secret);
        _cancellation = new CancellationTokenSource();

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
            RouterOpenedPort = mapping.Mapped;
            State = PartyState.Hosting;

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
                _clients.Add(hosted);

            Status?.Invoke(this, $"{name} joined.");

            SetPeer(name, null, isSelf: false, generation);
            await BroadcastAsync(cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await PartyProtocol.ReadAsync(stream, key, cancellationToken).ConfigureAwait(false);
                if (message is null)
                    break;

                if (message.Kind == PartyMessageKind.Ping && message.Position is { } ping)
                {
                    // Named from the connection, not the payload, so nobody can ping as somebody
                    // else.
                    ping.Name = name;

                    PingReceived?.Invoke(this, ping);
                    await RelayPingAsync(ping, hosted).ConfigureAwait(false);
                    continue;
                }

                if (message.Kind != PartyMessageKind.Position || message.Position is null)
                    continue;

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
            // Includes a frame that would not authenticate, which is what a wrong code looks like.
            Log.Warn($"party peer dropped: {ex.Message}");
        }
        finally
        {
            if (hosted is not null)
            {
                lock (_gate)
                {
                    _clients.Remove(hosted);
                    _peers.Remove(hosted.Name);
                }

                Status?.Invoke(this, $"{hosted.Name} left.");
                hosted.Dispose();

                await BroadcastAsync(CancellationToken.None).ConfigureAwait(false);
            }

            client.Dispose();
        }
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
            try
            {
                var message = new PartyMessage
                {
                    Kind = PartyMessageKind.Roster,
                    Name = client.Name,
                    Roster = roster,
                };

                await PartyProtocol.WriteAsync(client.Stream, key, message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"could not reach {client.Name}: {ex.Message}");
            }
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
                X = p.Position.X,
                Y = p.Position.Y,
                Z = p.Position.Z,
                Yaw = p.Yaw,

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
        _cancellation = new CancellationTokenSource();

        State = PartyState.Joining;
        Raise();

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
                new PartyMessage { Kind = PartyMessageKind.Hello, Name = _selfName },
                cancellationToken).ConfigureAwait(false);

            State = PartyState.Joined;
            Status?.Invoke(this, "Connected to the session.");
            Raise();

            _ = Task.Run(() => ReceiveLoopAsync(stream, _cancellation.Token), CancellationToken.None);

            // Anything already known locally goes up straight away, so the squad sees us without
            // waiting for the next screenshot.
            if (_selfPosition is not null)
                SendSelf();

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not join a party: {ex.Message}");
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

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await PartyProtocol
                    .ReadAsync(stream, _key!, cancellationToken)
                    .ConfigureAwait(false);

                if (message is null)
                    break;

                if (message.Kind == PartyMessageKind.Ping && message.Position is { } ping)
                {
                    PingReceived?.Invoke(this, ping);
                    continue;
                }

                if (message.Kind != PartyMessageKind.Roster || message.Roster is null)
                    continue;

                // The host is the authority on names, including ours if it had to disambiguate.
                if (!string.IsNullOrEmpty(message.Name))
                    _selfName = message.Name;

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
                    IsSelf = string.Equals(entry.Name, _selfName, StringComparison.OrdinalIgnoreCase),
                };
            }
        }
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
        };

        if (!IsActive)
            return;

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
    private void Send(PartyMessage message)
    {
        if (_upstream?.Connected != true || _key is not { } key)
            return;

        var stream = _upstream.GetStream();

        _ = Task.Run(async () =>
        {
            try
            {
                await PartyProtocol
                    .WriteAsync(stream, key, message, _cancellation?.Token ?? CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"could not send {message.Kind} to the host: {ex.Message}");
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
        Code = null;

        // Our own last position is session state too. Kept, it would be republished to whoever we
        // connect to next, before a screenshot has said we are anywhere.
        _selfPosition = null;

        if (State != PartyState.Failed)
            State = PartyState.Idle;

        Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Leave();
    }

    private sealed record HostedClient(TcpClient Client, NetworkStream Stream, string Name) : IDisposable
    {
        public void Dispose()
        {
            try { Stream.Dispose(); } catch (ObjectDisposedException) { }
            try { Client.Dispose(); } catch (ObjectDisposedException) { }
        }
    }
}
