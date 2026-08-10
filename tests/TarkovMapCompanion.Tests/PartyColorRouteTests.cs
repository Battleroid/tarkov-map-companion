using System.Text;
using System.Text.Json;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Party;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Colors and routes on the wire. Real sockets on loopback, like the rest of the party tests.
/// </summary>
public sealed class PartyColorRouteTests
{
    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(25);
        }

        return condition();
    }

    private static (double X, double Z)[] Route(params double[] xs) =>
        xs.Select(x => (x, x * 2)).ToArray();

    // ---- The tolerant kind converter ----------------------------------------

    /// <summary>
    /// The whole point. The stock converter throws on a name it has never seen, that exception
    /// comes out of ReadAsync, and both loops answer it by tearing the connection down -- so
    /// without this, a build that meets a newer message kind disconnects instead of skipping it.
    /// </summary>
    [Fact]
    public void AnUnknownMessageKindReadsAsUnknownRatherThanThrowing()
    {
        var json = """{"kind":"SomethingFromNextYear","name":"Rudmere"}""";

        var message = JsonSerializer.Deserialize<PartyMessage>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(message);
        Assert.Equal(PartyMessageKind.Unknown, message.Kind);
        Assert.Equal("Rudmere", message.Name);
    }

    /// <summary>Malformed rather than merely newer, but it still must not throw.</summary>
    [Theory]
    [InlineData("""{"kind":{"nested":true}}""")]
    [InlineData("""{"kind":[1,2,3]}""")]
    [InlineData("""{"kind":7}""")]
    [InlineData("""{"name":"nobody"}""")]
    public void AMalformedKindIsUnknownRatherThanFatal(string json)
    {
        var message = JsonSerializer.Deserialize<PartyMessage>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal(PartyMessageKind.Unknown, message!.Kind);
    }

    /// <summary>Known kinds still round-trip by name, which is what keeps the wire readable.</summary>
    [Fact]
    public void KnownKindsRoundTripByName()
    {
        foreach (var kind in Enum.GetValues<PartyMessageKind>())
        {
            var json = JsonSerializer.Serialize(new PartyMessage { Kind = kind });
            Assert.Contains(kind.ToString(), json, StringComparison.Ordinal);

            var back = JsonSerializer.Deserialize<PartyMessage>(json);
            Assert.Equal(kind, back!.Kind);
        }
    }

    // ---- Colors -------------------------------------------------------------

    [Fact]
    public async Task AChosenColorReachesTheOtherEnd()
    {
        using var host = new PartySession { SelfColor = "#F5C942" };
        using var guest = new PartySession { SelfColor = "#64B5F6" };

        await host.HostAsync("Host", 0, CancellationToken.None, useRouter: false);
        await guest.JoinAsync(host.Code!, "Guest");

        Assert.True(
            await Eventually(() => host.Peers.Any(p => p.Name == "Guest" && p.Color == "#64B5F6")),
            "the host never learned the guest's color");

        Assert.True(
            await Eventually(() => guest.Peers.Any(p => p.Name == "Host" && p.Color == "#F5C942")),
            "the guest never learned the host's color");
    }

    /// <summary>A change with no screenshot due to carry it still has to reach the squad.</summary>
    [Fact]
    public async Task ChangingColorMidSessionIsAnnounced()
    {
        using var host = new PartySession { SelfColor = "#F5C942" };
        using var guest = new PartySession { SelfColor = "#64B5F6" };

        await host.HostAsync("Host", 0, CancellationToken.None, useRouter: false);
        await guest.JoinAsync(host.Code!, "Guest");
        await Eventually(() => host.Peers.Count == 2);

        guest.SelfColor = "#BA68C8";

        Assert.True(
            await Eventually(() => host.Peers.Any(p => p.Name == "Guest" && p.Color == "#BA68C8")),
            "the color change never arrived");
    }

    [Fact]
    public async Task TheHostAnnouncesItsOwnColorChangeToo()
    {
        using var host = new PartySession { SelfColor = "#F5C942" };
        using var guest = new PartySession();

        await host.HostAsync("Host", 0, CancellationToken.None, useRouter: false);
        await guest.JoinAsync(host.Code!, "Guest");
        await Eventually(() => guest.Peers.Count == 2);

        host.SelfColor = "#81C784";

        Assert.True(
            await Eventually(() => guest.Peers.Any(p => p.Name == "Host" && p.Color == "#81C784")),
            "the host's color change never reached the guest");
    }

    /// <summary>
    /// Attribution is by connection, never by payload. Mirrors the same rule for pings: otherwise
    /// anybody in the session could recolor anybody else.
    /// </summary>
    [Fact]
    public async Task AColorCannotBeSetForSomebodyElse()
    {
        using var host = new PartySession { SelfColor = "#F5C942" };
        using var guest = new PartySession { SelfColor = "#64B5F6" };

        await host.HostAsync("Host", 0, CancellationToken.None, useRouter: false);
        await guest.JoinAsync(host.Code!, "Guest");
        await Eventually(() => host.Peers.Count == 2);

        await PartyProtocol.WriteAsync(
            guest.UpstreamStreamForTests!,
            guest.KeyForTests!,
            new PartyMessage { Kind = PartyMessageKind.Color, Name = "Host", Color = "#FF0000" });

        // The guest's own color moves; the host it named does not.
        Assert.True(await Eventually(() => host.Peers.Any(p => p.Name == "Guest" && p.Color == "#FF0000")));
        Assert.DoesNotContain(host.Peers, p => p.Name == "Host" && p.Color == "#FF0000");
    }

    // ---- Routes -------------------------------------------------------------

    [Fact]
    public async Task ARouteReachesTheOtherEnd()
    {
        using var host = new PartySession();
        using var guest = new PartySession();

        await host.HostAsync("Host", 0, CancellationToken.None, useRouter: false);
        await guest.JoinAsync(host.Code!, "Guest");
        await Eventually(() => host.Peers.Count == 2);

        guest.PublishRoute("customs", Route(10, 20, 30));

        Assert.True(
            await Eventually(() => host.Routes.Any(r => r.Name == "Guest" && r.Points.Count == 3)),
            "the host never received the route");

        Assert.True(
            await Eventually(() => guest.Routes.Any(r => r.Name == "Guest" && r.Points.Count == 3)),
            "the guest never got its own route back in the fan-out");
    }

    [Fact]
    public async Task TheHostsOwnRouteReachesTheGuests()
    {
        using var host = new PartySession();
        using var guest = new PartySession();

        await host.HostAsync("Host", 0, CancellationToken.None, useRouter: false);
        await guest.JoinAsync(host.Code!, "Guest");
        await Eventually(() => guest.Peers.Count == 2);

        host.PublishRoute("customs", Route(1, 2));

        Assert.True(
            await Eventually(() => guest.Routes.Any(r => r.Name == "Host" && r.Points.Count == 2)),
            "the host's route never reached the guest");
    }

    /// <summary>
    /// The classic bug in this feature shape: treating "nothing to say" as "say nothing" leaves a
    /// phantom route drawn on every teammate's map until the session ends.
    /// </summary>
    [Fact]
    public async Task ClearingARoutePublishesAnEmptyOneAndTheFarEndDropsIt()
    {
        using var host = new PartySession();
        using var guest = new PartySession();

        await host.HostAsync("Host", 0, CancellationToken.None, useRouter: false);
        await guest.JoinAsync(host.Code!, "Guest");
        await Eventually(() => host.Peers.Count == 2);

        guest.PublishRoute("customs", Route(10, 20));
        await Eventually(() => host.Routes.Any(r => r.Name == "Guest"));

        guest.PublishRoute("customs", []);

        Assert.True(
            await Eventually(() => host.Routes.All(r => r.Name != "Guest")),
            "the host kept a route that had been cleared");
    }

    [Fact]
    public async Task ALateJoinerReceivesRoutesThatAlreadyExist()
    {
        using var host = new PartySession();
        using var first = new PartySession();
        using var late = new PartySession();

        await host.HostAsync("Host", 0, CancellationToken.None, useRouter: false);
        await first.JoinAsync(host.Code!, "First");
        await Eventually(() => host.Peers.Count == 2);

        first.PublishRoute("customs", Route(5, 15, 25));
        await Eventually(() => host.Routes.Any(r => r.Name == "First"));

        await late.JoinAsync(host.Code!, "Late");

        Assert.True(
            await Eventually(() => late.Routes.Any(r => r.Name == "First" && r.Points.Count == 3)),
            "a late joiner started with a blank map");
    }

    [Fact]
    public async Task ARouteCannotBeSentUnderSomebodyElsesName()
    {
        using var host = new PartySession();
        using var guest = new PartySession();

        await host.HostAsync("Host", 0, CancellationToken.None, useRouter: false);
        await guest.JoinAsync(host.Code!, "Guest");
        await Eventually(() => host.Peers.Count == 2);

        await PartyProtocol.WriteAsync(
            guest.UpstreamStreamForTests!,
            guest.KeyForTests!,
            new PartyMessage
            {
                Kind = PartyMessageKind.Route,
                Route = new PeerRoute
                {
                    Name = "Host",
                    Map = "customs",
                    Points = [new RoutePoint { X = 1, Z = 1 }],
                },
            });

        Assert.True(await Eventually(() => host.Routes.Any(r => r.Name == "Guest")));
        Assert.DoesNotContain(host.Routes, r => r.Name == "Host");
    }

    /// <summary>
    /// An over-size frame is rejected by the receiver, and the host answers a rejected frame by
    /// dropping the peer -- so an unbounded route is a way to disconnect your squad by drawing.
    /// </summary>
    [Fact]
    public async Task AnOverlongRouteIsTruncatedRatherThanDroppingThePeer()
    {
        using var host = new PartySession();
        using var guest = new PartySession();

        await host.HostAsync("Host", 0, CancellationToken.None, useRouter: false);
        await guest.JoinAsync(host.Code!, "Guest");
        await Eventually(() => host.Peers.Count == 2);

        guest.PublishRoute("customs", Route(Enumerable.Range(0, 500).Select(i => (double)i).ToArray()));

        Assert.True(
            await Eventually(() =>
                host.Routes.Any(r => r.Name == "Guest" && r.Points.Count == PartyProtocol.MaxRoutePoints)),
            "the route was not truncated to the cap");

        // And the peer is still there, which is the part that actually matters.
        Assert.Contains(host.Peers, p => p.Name == "Guest");
    }

    [Fact]
    public async Task EndingASessionForgetsEveryRoute()
    {
        using var host = new PartySession();
        using var guest = new PartySession();

        await host.HostAsync("Host", 0, CancellationToken.None, useRouter: false);
        await guest.JoinAsync(host.Code!, "Guest");
        await Eventually(() => host.Peers.Count == 2);

        guest.PublishRoute("customs", Route(10, 20));
        await Eventually(() => host.Routes.Any(r => r.Name == "Guest"));

        host.Leave();

        Assert.Empty(host.Routes);
    }

    /// <summary>A route belonging to a session that has ended must not be applied late.</summary>
    [Fact]
    public void ARouteBelongingToAnEndedSessionIsDiscarded()
    {
        using var session = new PartySession();
        var stale = session.CurrentGeneration;

        session.Leave();

        session.ApplyRoutes(
            [new PeerRoute { Name = "Ghost", Map = "customs", Points = [new RoutePoint { X = 1, Z = 1 }] }],
            stale);

        Assert.Empty(session.Routes);
    }

    // ---- Frame budget -------------------------------------------------------

    /// <summary>
    /// Refused on the way out rather than left for the receiver, which reads it as corruption.
    /// </summary>
    [Fact]
    public async Task AnOverSizeFrameIsRefusedRatherThanSent()
    {
        var key = PartyProtocol.DeriveKey(SessionCode.NewSecret());
        using var stream = new MemoryStream();

        var huge = new PartyMessage
        {
            Kind = PartyMessageKind.Ping,
            Name = new string('x', PartyProtocol.MaxFrameBytes * 2),
        };

        await PartyProtocol.WriteAsync(stream, key, huge);

        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task AFrameInsideTheBudgetIsStillSent()
    {
        var key = PartyProtocol.DeriveKey(SessionCode.NewSecret());
        using var stream = new MemoryStream();

        await PartyProtocol.WriteAsync(stream, key, new PartyMessage { Kind = PartyMessageKind.Ping });

        Assert.True(stream.Length > 0);
    }

    /// <summary>
    /// The version stamp is not gated on, but it has to actually be on the wire or the next change
    /// has nothing to compare against.
    /// </summary>
    [Fact]
    public async Task EverySentMessageCarriesTheProtocolVersion()
    {
        var key = PartyProtocol.DeriveKey(SessionCode.NewSecret());
        using var stream = new MemoryStream();

        await PartyProtocol.WriteAsync(stream, key, new PartyMessage { Kind = PartyMessageKind.Hello });

        stream.Position = 0;
        var read = await PartyProtocol.ReadAsync(stream, key);

        Assert.Equal(PartyProtocol.Version, read!.Version);
    }

    /// <summary>
    /// The salt is the only version marker the wire has. Moving it is what makes an older build
    /// fail at the handshake instead of connecting and then dropping somebody mid-session.
    /// </summary>
    /// <remarks>
    /// This failing is the point: it means somebody changed the salt, and it should cost a
    /// deliberate edit here rather than happening by accident. Moved to v3 for heartbeats, because
    /// a build that does not answer them would either be dropped every twenty seconds or have to be
    /// exempted from the check that makes them worth having.
    /// </remarks>
    [Fact]
    public void TheKeyDerivationSaltIsPinnedToTheCurrentProtocol()
    {
        var secret = new byte[] { 1, 2, 3, 4, 5, 6 };

        var expected = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            secret,
            Encoding.UTF8.GetBytes("TarkovMapCompanion/party/v3"),
            100_000,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            32);

        Assert.Equal(expected, PartyProtocol.DeriveKey(secret));
    }
}
