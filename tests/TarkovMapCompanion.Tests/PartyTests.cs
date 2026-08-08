using System.Net;
using System.Security.Cryptography;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Party;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// The code a host reads out to the squad.
/// </summary>
public sealed class SessionCodeTests
{
    [Fact]
    public void ACodeRoundTripsBackToTheEndpointAndSecret()
    {
        var secret = SessionCode.NewSecret();
        var code = SessionCode.Format(IPAddress.Parse("203.0.113.42"), 24601, secret);

        Assert.True(SessionCode.TryParse(code, out var endPoint, out var parsed));

        Assert.Equal("203.0.113.42", endPoint.Address.ToString());
        Assert.Equal(24601, endPoint.Port);
        Assert.Equal(secret, parsed);
    }

    [Fact]
    public void CodesAreGroupedAndUseAnUnambiguousAlphabet()
    {
        var code = SessionCode.Format(IPAddress.Parse("198.51.100.7"), 40000, SessionCode.NewSecret());

        Assert.Equal(23, code.Length);
        Assert.Equal(4, code.Split('-').Length);

        // I, L, O and U are excluded so nothing can be confused for a digit when read aloud.
        Assert.DoesNotContain(code, c => c is 'I' or 'L' or 'O' or 'U');
    }

    [Theory]
    [InlineData("lowercase")]
    [InlineData("nohyphens")]
    [InlineData("spaces")]
    [InlineData("confusable")]
    public void CodesSurviveBeingRetypedByHand(string mangling)
    {
        var secret = SessionCode.NewSecret();
        var original = SessionCode.Format(IPAddress.Parse("192.0.2.9"), 1234, secret);

        var mangled = mangling switch
        {
            "lowercase" => original.ToLowerInvariant(),
            "nohyphens" => original.Replace("-", ""),
            "spaces" => original.Replace("-", " "),

            // Crockford folds O to zero and I/L to one, which is exactly what someone will type.
            "confusable" => original.Replace('0', 'O').Replace('1', 'I'),
            _ => original,
        };

        Assert.True(SessionCode.TryParse(mangled, out var endPoint, out var parsed), mangling);
        Assert.Equal(1234, endPoint.Port);
        Assert.Equal(secret, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-code")]
    [InlineData("K7QM4-3FHB9-8TZR9")]
    [InlineData("K7QM4-3FHB9-8TZR9-M4X7Q-EXTRA")]
    public void RubbishIsRejectedRatherThanGuessedAt(string? code)
    {
        Assert.False(SessionCode.TryParse(code, out _, out _));
    }

    [Fact]
    public void EveryHighPortSurvivesTheRoundTrip()
    {
        // The port is packed as two bytes; anything above 32767 would come back negative if it
        // were ever treated as signed.
        foreach (var port in new[] { 1, 1024, 24601, 32768, 49152, 65535 })
        {
            var code = SessionCode.Format(IPAddress.Loopback, port, SessionCode.NewSecret());

            Assert.True(SessionCode.TryParse(code, out var endPoint, out _));
            Assert.Equal(port, endPoint.Port);
        }
    }
}

/// <summary>
/// Framing and encryption on the wire.
/// </summary>
public sealed class PartyProtocolTests
{
    private static byte[] Key(string seed) => PartyProtocol.DeriveKey(System.Text.Encoding.UTF8.GetBytes(seed));

    [Fact]
    public async Task AMessageRoundTripsThroughAStream()
    {
        var key = Key("shared");

        var sent = new PartyMessage
        {
            Kind = PartyMessageKind.Position,
            Position = new PeerPosition { Name = "Casey", Map = "customs", X = 1.5, Y = 2.5, Z = -3.5, Yaw = 90 },
        };

        using var stream = new MemoryStream();
        await PartyProtocol.WriteAsync(stream, key, sent);

        stream.Position = 0;
        var received = await PartyProtocol.ReadAsync(stream, key);

        Assert.NotNull(received);
        Assert.Equal(PartyMessageKind.Position, received.Kind);
        Assert.Equal("Casey", received.Position?.Name);
        Assert.Equal(-3.5, received.Position?.Z);
    }

    [Fact]
    public async Task TheWrongKeyCannotReadTheFrame()
    {
        // This is the whole of authentication: holding the secret from the code is what being
        // invited means, so a wrong code has to fail here rather than anywhere subtler.
        using var stream = new MemoryStream();

        await PartyProtocol.WriteAsync(
            stream,
            Key("correct"),
            new PartyMessage { Kind = PartyMessageKind.Hello, Name = "Casey" },
            CancellationToken.None);

        stream.Position = 0;

        // ThrowsAny, because a failed tag check surfaces as AuthenticationTagMismatchException,
        // which is a CryptographicException but not exactly one.
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            PartyProtocol.ReadAsync(stream, Key("wrong")));
    }

    [Fact]
    public async Task ATamperedFrameIsRejected()
    {
        var key = Key("shared");

        using var stream = new MemoryStream();
        await PartyProtocol.WriteAsync(
            stream,
            key,
            new PartyMessage { Kind = PartyMessageKind.Hello, Name = "Casey" },
            CancellationToken.None);

        var bytes = stream.ToArray();
        bytes[^1] ^= 0xFF;

        using var tampered = new MemoryStream(bytes);

        // ThrowsAny, because a failed tag check surfaces as AuthenticationTagMismatchException,
        // which is a CryptographicException but not exactly one.
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            PartyProtocol.ReadAsync(tampered, key));
    }

    [Fact]
    public async Task AnAbsurdFrameLengthIsRefusedBeforeAllocating()
    {
        var header = new byte[] { 0x7F, 0xFF, 0xFF, 0xFF };
        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PartyProtocol.ReadAsync(stream, Key("shared")));
    }

    [Fact]
    public async Task AClosedStreamReadsAsEndRatherThanThrowing()
    {
        using var stream = new MemoryStream();

        Assert.Null(await PartyProtocol.ReadAsync(stream, Key("shared")));
    }

    [Theory]
    [InlineData(null, "Player")]
    [InlineData("   ", "Player")]
    [InlineData("  Casey  ", "Casey")]
    [InlineData("a-very-long-name-indeed", "a-very-long-name")]
    public void NamesAreTrimmedToSomethingThatFitsOnAMarker(string? input, string expected)
    {
        Assert.Equal(expected, PartyProtocol.CleanName(input));
    }

    [Fact]
    public void ControlCharactersCannotBeSmuggledIntoAName()
    {
        Assert.Equal("bad name", PartyProtocol.CleanName("bad\r\nname"));
    }
}

/// <summary>
/// Two sessions talking over real sockets on loopback.
/// </summary>
/// <remarks>
/// Nothing is stubbed here: a real listener, a real TCP connection, the real encrypted protocol.
/// The only concession is that hosting skips the router, since a unit test cannot depend on the
/// machine having a UPnP gateway.
/// </remarks>
public sealed class PartySessionTests
{
    private static GamePosition At(double x, double z) => new(x, 1.5, z);

    /// <summary>Waits for a condition rather than sleeping, so the tests are neither slow nor flaky.</summary>
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

    [Fact]
    public async Task APeerJoinsAndBothSidesSeeEachOther()
    {
        using var host = new PartySession();
        using var guest = new PartySession();

        Assert.True(await host.HostAsync("Host", CancellationToken.None, useRouter: false));
        Assert.NotNull(host.Code);

        Assert.True(await guest.JoinAsync(host.Code, "Guest"));

        Assert.True(await Eventually(() => host.Peers.Count == 2), "host never saw the guest");
        Assert.True(await Eventually(() => guest.Peers.Count == 2), "guest never got a roster");

        Assert.Contains(host.Peers, p => p.Name == "Guest");
        Assert.Contains(guest.Peers, p => p.Name == "Host");
    }

    [Fact]
    public async Task PositionsFlowBothWays()
    {
        using var host = new PartySession();
        using var guest = new PartySession();

        await host.HostAsync("Host", CancellationToken.None, useRouter: false);
        await guest.JoinAsync(host.Code!, "Guest");

        await Eventually(() => guest.Peers.Count == 2);

        host.Publish("customs", At(100, 200), 90);
        guest.Publish("customs", At(-50, 75), 180);

        Assert.True(
            await Eventually(() => host.Peers.Any(p => p.Name == "Guest" && p.HasPosition)),
            "the guest's position never reached the host");

        Assert.True(
            await Eventually(() => guest.Peers.Any(p => p.Name == "Host" && p.Position.X == 100)),
            "the host's position never reached the guest");

        var seenByGuest = guest.Peers.Single(p => p.Name == "Host");
        Assert.Equal("customs", seenByGuest.Map);
        Assert.Equal(200, seenByGuest.Position.Z);
        Assert.Equal(90, seenByGuest.Yaw);
    }

    [Fact]
    public async Task JoiningMidSessionHandsOverEverybodyAtOnce()
    {
        // The reason the host broadcasts a whole roster rather than deltas: a late joiner should
        // have the squad immediately, not after waiting for each of them to take a screenshot.
        using var host = new PartySession();
        using var first = new PartySession();
        using var late = new PartySession();

        await host.HostAsync("Host", CancellationToken.None, useRouter: false);
        await first.JoinAsync(host.Code!, "First");

        await Eventually(() => host.Peers.Count == 2);

        host.Publish("customs", At(10, 20), 0);
        first.Publish("customs", At(30, 40), 45);

        await Eventually(() => host.Peers.Count(p => p.HasPosition) == 2);

        Assert.True(await late.JoinAsync(host.Code!, "Late"));

        Assert.True(
            await Eventually(() => late.Peers.Count(p => p.HasPosition) == 2),
            "the late joiner did not receive the existing positions");

        Assert.Contains(late.Peers, p => p.Name == "Host" && p.Position.X == 10);
        Assert.Contains(late.Peers, p => p.Name == "First" && p.Position.X == 30);
    }

    [Fact]
    public async Task TwoPeopleWithTheSameNameAreToldApart()
    {
        using var host = new PartySession();
        using var a = new PartySession();
        using var b = new PartySession();

        await host.HostAsync("Host", CancellationToken.None, useRouter: false);
        await a.JoinAsync(host.Code!, "Twin");
        await Eventually(() => host.Peers.Count == 2);

        await b.JoinAsync(host.Code!, "Twin");
        Assert.True(await Eventually(() => host.Peers.Count == 3), "the second Twin displaced the first");

        Assert.Equal(3, host.Peers.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task TheWrongCodeCannotJoin()
    {
        using var host = new PartySession();
        using var intruder = new PartySession();

        await host.HostAsync("Host", CancellationToken.None, useRouter: false);

        // Same endpoint, different secret: reachable, but unable to say anything the host will read.
        Assert.True(SessionCode.TryParse(host.Code, out var endPoint, out _));
        var wrong = SessionCode.Format(endPoint.Address, endPoint.Port, SessionCode.NewSecret());

        await intruder.JoinAsync(wrong, "Intruder");

        // The TCP connection succeeds; the host drops it once the first frame fails to
        // authenticate, so the intruder never appears in the roster.
        await Task.Delay(500);
        Assert.DoesNotContain(host.Peers, p => p.Name == "Intruder");
    }

    [Fact]
    public async Task LeavingReleasesEverythingAndCanBeRestarted()
    {
        // Restarting is the first thing anyone tries when something is wrong, so it has to work
        // from any state rather than only the ones somebody thought of.
        using var host = new PartySession();

        await host.HostAsync("Host", CancellationToken.None, useRouter: false);
        var first = host.Code;

        host.Leave();

        Assert.Equal(PartyState.Idle, host.State);
        Assert.Null(host.Code);
        Assert.Empty(host.Peers);

        Assert.True(await host.HostAsync("Host", CancellationToken.None, useRouter: false));
        Assert.NotNull(host.Code);
        Assert.NotEqual(first, host.Code);

        host.Leave();
        host.Leave();
    }

    [Fact]
    public async Task AGuestIsDroppedFromTheRosterWhenItDisconnects()
    {
        using var host = new PartySession();
        var guest = new PartySession();

        await host.HostAsync("Host", CancellationToken.None, useRouter: false);
        await guest.JoinAsync(host.Code!, "Guest");

        await Eventually(() => host.Peers.Count == 2);

        guest.Dispose();

        Assert.True(await Eventually(() => host.Peers.Count == 1), "the host kept a peer that had gone");
    }

    [Fact]
    public async Task AnUnreachableHostFailsInsteadOfHanging()
    {
        using var guest = new PartySession();

        // Nothing is listening on this port on loopback.
        var code = SessionCode.Format(IPAddress.Loopback, 9, SessionCode.NewSecret());

        Assert.False(await guest.JoinAsync(code, "Guest"));
        Assert.Equal(PartyState.Failed, guest.State);
    }
}
