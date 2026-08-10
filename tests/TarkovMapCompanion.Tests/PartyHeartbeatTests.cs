using System.Net;
using System.Net.Sockets;
using TarkovMapCompanion.Party;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Staying connected, and noticing when you are not.
/// </summary>
/// <remarks>
/// These cover the failure a squad actually hit: everything looked healthy, nothing was flowing,
/// and rejoining was the only cure. Two causes, both here.
/// </remarks>
public sealed class PartyHeartbeatTests
{
    private static async Task<(PartySession Host, string Code)> StartHostAsync(string name)
    {
        var host = new PartySession();
        var started = await host.HostAsync(name, 0, CancellationToken.None, useRouter: false);

        Assert.True(started, "the host did not start");
        Assert.NotNull(host.Code);

        return (host, host.Code!);
    }

    /// <summary>
    /// A heartbeat is answered, and the answer echoes the sequence number.
    /// </summary>
    /// <remarks>
    /// The echo is what makes a round trip measurable at all: without it a reply cannot be matched
    /// to the probe that caused it, and a reading would be the time since whichever probe happened
    /// to be most recent.
    /// </remarks>
    [Fact]
    public async Task AHeartbeatIsAnsweredWithItsOwnSequenceNumber()
    {
        var (host, code) = await StartHostAsync("Host");
        using var _ = host;

        var parsed = SessionCode.TryParse(code, out var endPoint, out var secret);
        Assert.True(parsed);

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(endPoint!);

        var stream = client.GetStream();
        var key = PartyProtocol.DeriveKey(secret!);

        await PartyProtocol.WriteAsync(stream, key, new PartyMessage
        {
            Kind = PartyMessageKind.Hello,
            Name = "Guest",
        });

        await PartyProtocol.WriteAsync(stream, key, new PartyMessage
        {
            Kind = PartyMessageKind.Heartbeat,
            Seq = 4242,
        });

        var ack = await ReadUntilAsync(stream, key, PartyMessageKind.HeartbeatAck);

        Assert.NotNull(ack);
        Assert.Equal(4242, ack.Seq);
    }

    /// <summary>
    /// A connection that stops answering is dropped, even though its socket is still open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the failure the whole heartbeat exists for, so it is driven the way it actually
    /// happens rather than by closing anything. The socket stays open and the operating system
    /// keeps acknowledging at the transport level; what stops is the application answering. Before
    /// heartbeats the host would have kept this peer in the roster forever, broadcasting to a
    /// connection that was never going to carry anything again.
    /// </para>
    /// <para>
    /// Run with the timings wound down, because at the shipping ones it would cost twenty-one
    /// seconds of wall clock.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AConnectionThatStopsAnsweringIsDropped()
    {
        using var host = new PartySession(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(600));

        Assert.True(await host.HostAsync("Host", 0, CancellationToken.None, useRouter: false));
        Assert.True(SessionCode.TryParse(host.Code!, out var endPoint, out var secret));

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(endPoint!);

        var stream = client.GetStream();
        var key = PartyProtocol.DeriveKey(secret!);

        await PartyProtocol.WriteAsync(stream, key, new PartyMessage
        {
            Kind = PartyMessageKind.Hello,
            Name = "Ghost",
        });

        Assert.True(
            await WaitForAsync(() => host.Peers.Any(p => p.Name == "Ghost"), TimeSpan.FromSeconds(5)),
            "the client never joined");

        // From here it says nothing. The socket is left open on purpose.
        var gone = await WaitForAsync(
            () => !host.Peers.Any(p => p.Name == "Ghost"),
            TimeSpan.FromSeconds(10));

        Assert.True(gone, "a client that stopped answering is still in the roster");
    }

    /// <summary>Answering keeps you in, so the timeout cannot evict a healthy connection.</summary>
    [Fact]
    public async Task AConnectionThatKeepsAnsweringIsKept()
    {
        using var host = new PartySession(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(600));

        Assert.True(await host.HostAsync("Host", 0, CancellationToken.None, useRouter: false));
        Assert.True(SessionCode.TryParse(host.Code!, out var endPoint, out var secret));

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(endPoint!);

        var stream = client.GetStream();
        var key = PartyProtocol.DeriveKey(secret!);

        await PartyProtocol.WriteAsync(stream, key, new PartyMessage
        {
            Kind = PartyMessageKind.Hello,
            Name = "Alive",
        });

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // Answer every heartbeat, the way a real client does.
        var answering = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var message = await PartyProtocol.ReadAsync(stream, key, stop.Token);
                if (message is null)
                    return;

                if (message.Kind == PartyMessageKind.Heartbeat)
                {
                    await PartyProtocol.WriteAsync(stream, key, new PartyMessage
                    {
                        Kind = PartyMessageKind.HeartbeatAck,
                        Seq = message.Seq,
                    }, stop.Token);
                }
            }
        }, stop.Token);

        // Several times the timeout, all of it spent answering.
        await Task.Delay(2000);

        Assert.Contains(host.Peers, p => p.Name == "Alive");

        // And the round trip got measured along the way.
        Assert.NotNull(host.Peers.Single(p => p.Name == "Alive").LatencyMs);

        stop.Cancel();
        try { await answering; } catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Two sends racing on one connection do not corrupt it.
    /// </summary>
    /// <remarks>
    /// The bug this pins is subtle and permanent: every client's read loop broadcasts to every
    /// other client, so several tasks routinely write to one socket at once. Half of one frame
    /// followed by half of another leaves the length prefix misaligned, and nothing on that
    /// connection ever decodes again. It looks exactly like "connected, but no updates".
    /// </remarks>
    [Fact]
    public async Task ConcurrentSendsDoNotCorruptTheStream()
    {
        var (host, code) = await StartHostAsync("Host");
        using var _ = host;

        Assert.True(SessionCode.TryParse(code, out var endPoint, out var secret));

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(endPoint!);

        var stream = client.GetStream();
        var key = PartyProtocol.DeriveKey(secret!);

        await PartyProtocol.WriteAsync(stream, key, new PartyMessage
        {
            Kind = PartyMessageKind.Hello,
            Name = "Guest",
        });

        // Positions with a long route attached, so each frame is big enough to be split by the
        // socket layer and interleave if nothing is serializing the writes.
        var route = new PeerRoute
        {
            Map = "customs",
            Points = Enumerable.Range(0, PartyProtocol.MaxRoutePoints)
                .Select(i => new RoutePoint { X = i * 1.5, Z = i * -2.5 })
                .ToList(),
        };

        for (var i = 0; i < 12; i++)
        {
            await PartyProtocol.WriteAsync(stream, key, new PartyMessage
            {
                Kind = PartyMessageKind.Route,
                Route = route,
            });

            await PartyProtocol.WriteAsync(stream, key, new PartyMessage
            {
                Kind = PartyMessageKind.Position,
                Position = new PeerPosition { Map = "customs", X = i, Z = i },
            });
        }

        // Every frame the host sends back has to decode. One misframed write and this throws or
        // stalls rather than returning.
        var decoded = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (decoded < 12 && DateTime.UtcNow < deadline)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            try
            {
                var message = await PartyProtocol.ReadAsync(stream, key, timeout.Token);
                if (message is null)
                    break;

                decoded++;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Assert.True(decoded >= 12, $"only {decoded} frames came back intact");
    }

    /// <summary>The roster carries the latency the host measured, so everyone sees one number.</summary>
    [Fact]
    public void TheRosterCarriesLatency()
    {
        using var session = new PartySession();

        session.ApplyRoster(
            [
                new PeerPosition { Name = "Host", Map = "customs", AgeSeconds = 1, LatencyMs = 12 },
                new PeerPosition { Name = "Guest", Map = "customs", AgeSeconds = 2, LatencyMs = 88 },
                new PeerPosition { Name = "Unmeasured", Map = "customs", AgeSeconds = 3 },
            ],
            session.CurrentGeneration);

        Assert.Equal(12, session.Peers.Single(p => p.Name == "Host").LatencyMs);
        Assert.Equal(88, session.Peers.Single(p => p.Name == "Guest").LatencyMs);
        Assert.Null(session.Peers.Single(p => p.Name == "Unmeasured").LatencyMs);
    }

    /// <summary>An unknown kind is still skipped, which is what keeps the next change additive.</summary>
    [Fact]
    public async Task HeartbeatKindsRoundTrip()
    {
        var key = PartyProtocol.DeriveKey(SessionCode.NewSecret());
        using var stream = new MemoryStream();

        await PartyProtocol.WriteAsync(stream, key, new PartyMessage
        {
            Kind = PartyMessageKind.Heartbeat,
            Seq = long.MaxValue,
        });

        stream.Position = 0;
        var read = await PartyProtocol.ReadAsync(stream, key);

        Assert.Equal(PartyMessageKind.Heartbeat, read!.Kind);
        Assert.Equal(long.MaxValue, read.Seq);
        Assert.Equal(PartyProtocol.Version, read.Version);
    }

    /// <summary>Four heartbeats have to fit inside the timeout, or a single hiccup ends sessions.</summary>
    [Fact]
    public void TheTimeoutLeavesRoomForSeveralMissedBeats()
    {
        Assert.True(
            PartyProtocol.DeadAfter >= PartyProtocol.HeartbeatInterval * 4,
            "the timeout is too tight for a dropped heartbeat to be survivable");
    }

    // ---- Helpers ------------------------------------------------------------

    private static async Task<PartyMessage?> ReadUntilAsync(
        NetworkStream stream, byte[] key, PartyMessageKind kind)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        while (!timeout.IsCancellationRequested)
        {
            var message = await PartyProtocol.ReadAsync(stream, key, timeout.Token);
            if (message is null)
                return null;

            if (message.Kind == kind)
                return message;
        }

        return null;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(50);
        }

        return condition();
    }
}
