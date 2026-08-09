using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Party;
using TarkovMapCompanion.Rendering;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Peer trails are fed by the roster rather than by a movement event, and the roster is rebroadcast
/// every time anybody in the squad publishes. Most of what matters here is about not mistaking that
/// firehose for movement.
/// </summary>
public class PeerTrailTests
{
    private static readonly GameMap Customs = MapCatalog.LoadEmbedded().Resolve("customs");

    private static PartyPeer Peer(string name, double x, double z, string map = "customs") => new()
    {
        Name = name,
        Map = map,
        Position = new GamePosition(x, 0, z),
        Yaw = 0,
        HasPosition = true,
    };

    private static PeerOverlay NewOverlay(int trailLength = 5) =>
        new() { Map = Customs, TrailLength = trailLength };

    /// <summary>
    /// The whole roster is rebroadcast whenever anybody publishes, so a stationary teammate would
    /// otherwise accumulate a trail made entirely of copies of one spot.
    /// </summary>
    [Fact]
    public void AStationaryTeammateDoesNotAccumulateATrail()
    {
        var overlay = NewOverlay();

        for (var i = 0; i < 10; i++)
            overlay.SetPeers([Peer("Rudmere", 100, 100)]);

        Assert.Single(overlay.TrackForTests("Rudmere"));
    }

    [Fact]
    public void AMoveShorterThanTheSpacingIsIgnored()
    {
        var overlay = NewOverlay();

        overlay.SetPeers([Peer("Rudmere", 0, 0)]);
        overlay.SetPeers([Peer("Rudmere", 0, 10)]);
        overlay.SetPeers([Peer("Rudmere", 0, 20)]);

        Assert.Single(overlay.TrackForTests("Rudmere"));
    }

    [Fact]
    public void RealMovementIsRecorded()
    {
        var overlay = NewOverlay();

        overlay.SetPeers([Peer("Rudmere", 0, 0)]);
        overlay.SetPeers([Peer("Rudmere", 0, 40)]);
        overlay.SetPeers([Peer("Rudmere", 0, 80)]);

        Assert.Equal(3, overlay.TrackForTests("Rudmere").Length);
    }

    [Fact]
    public void TheTrailIsCappedAtTheConfiguredLength()
    {
        var overlay = NewOverlay(trailLength: 3);

        for (var i = 0; i < 10; i++)
            overlay.SetPeers([Peer("Rudmere", 0, i * 40)]);

        var trail = overlay.TrackForTests("Rudmere");

        Assert.Equal(3, trail.Length);

        // Oldest first, and it is the newest three that survive.
        Assert.Equal(280, trail[0].Z);
        Assert.Equal(360, trail[^1].Z);
    }

    [Fact]
    public void ZeroLengthTurnsTrailsOffEntirely()
    {
        var overlay = NewOverlay(trailLength: 0);

        overlay.SetPeers([Peer("Rudmere", 0, 0)]);
        overlay.SetPeers([Peer("Rudmere", 0, 40)]);

        Assert.Empty(overlay.TrackForTests("Rudmere"));
    }

    /// <summary>
    /// Leaving a session empties the roster, so pruning on absence is what makes an ended session
    /// clean up after itself rather than leaving a trail drawn across the map.
    /// </summary>
    [Fact]
    public void ATeammateWhoLeavesLosesTheirTrail()
    {
        var overlay = NewOverlay();

        overlay.SetPeers([Peer("Rudmere", 0, 0), Peer("Casey", 50, 50)]);
        overlay.SetPeers([Peer("Rudmere", 0, 40), Peer("Casey", 90, 90)]);

        overlay.SetPeers([Peer("Casey", 130, 130)]);

        Assert.Empty(overlay.TrackForTests("Rudmere"));
        Assert.NotEmpty(overlay.TrackForTests("Casey"));
    }

    [Fact]
    public void EndingASessionClearsEveryTrail()
    {
        var overlay = NewOverlay();

        overlay.SetPeers([Peer("Rudmere", 0, 0)]);
        overlay.SetPeers([Peer("Rudmere", 0, 40)]);

        overlay.SetPeers([]);

        Assert.Empty(overlay.TrackForTests("Rudmere"));
    }

    /// <summary>A trail is in one map's coordinates and is meaningless in another's.</summary>
    [Fact]
    public void ChangingMapClearsEveryTrail()
    {
        var overlay = NewOverlay();

        overlay.SetPeers([Peer("Rudmere", 0, 0)]);
        overlay.SetPeers([Peer("Rudmere", 0, 40)]);

        overlay.Map = MapCatalog.LoadEmbedded().Resolve("shoreline");

        Assert.Empty(overlay.TrackForTests("Rudmere"));
    }

    /// <summary>A teammate taking a transit is on new ground; the old points describe elsewhere.</summary>
    [Fact]
    public void ATeammateTransitingLosesTheirOldPoints()
    {
        var overlay = NewOverlay();

        overlay.SetPeers([Peer("Rudmere", 0, 0)]);
        overlay.SetPeers([Peer("Rudmere", 0, 40)]);

        overlay.SetPeers([Peer("Rudmere", 0, 80, map: "shoreline")]);

        Assert.Empty(overlay.TrackForTests("Rudmere"));
        Assert.Single(overlay.TrackForTests("Rudmere", "shoreline"));
    }

    /// <summary>Names are the identity everywhere else in the party layer, case-insensitively.</summary>
    [Fact]
    public void TrailsAreKeyedByNameWithoutRegardToCase()
    {
        var overlay = NewOverlay();

        overlay.SetPeers([Peer("Rudmere", 0, 0)]);
        overlay.SetPeers([Peer("RUDMERE", 0, 40)]);

        Assert.Equal(2, overlay.TrackForTests("rudmere").Length);
    }

    [Fact]
    public void YourOwnEntryNeverGetsATrail()
    {
        var overlay = NewOverlay();

        overlay.SetPeers([new PartyPeer
        {
            Name = "Casey",
            Map = "customs",
            Position = new GamePosition(0, 0, 0),
            Yaw = 0,
            HasPosition = true,
            IsSelf = true,
        }]);

        Assert.Empty(overlay.TrackForTests("Casey"));
    }
}
