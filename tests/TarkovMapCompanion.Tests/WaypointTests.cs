using TarkovMapCompanion.Data;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// The ordered route a player draws on the map, and how markers retire once reached.
/// </summary>
public sealed class WaypointTests
{
    private static WaypointOverlay Route(params (double X, double Z)[] points)
    {
        var overlay = new WaypointOverlay { ArrivalRadiusMeters = 50 };

        foreach (var (x, z) in points)
            overlay.Add(new GamePosition(x, 0, z), new MapPoint(x, z));

        return overlay;
    }

    private static GamePosition At(double x, double z) => new(x, 0, z);

    [Fact]
    public void MarkersAreNumberedInThePlacedOrder()
    {
        var route = Route((0, 0), (100, 0), (200, 0));

        Assert.Equal([1, 2, 3], route.Waypoints.Select(w => w.Number));
    }

    [Fact]
    public void TheNextMarkerIsTheFirstNotYetReached()
    {
        var route = Route((0, 0), (500, 0));

        Assert.Equal(1, route.Next?.Number);
    }

    [Fact]
    public void ArrivingMarksTheMarkerAndTheNextUpdateRemovesIt()
    {
        // The default: one screenshot showing it as reached, then gone. The confirmation is the
        // point -- a pin that just vanishes leaves you unsure whether you got close enough.
        var route = Route((0, 0), (500, 0));

        Assert.True(route.ApplyFix(At(10, 0)));

        var reached = Assert.Single(route.Waypoints, w => w.Visited);
        Assert.Equal(2, route.Waypoints.Count);
        Assert.Equal(1, reached.Number);

        // Routing has already moved on, even though the pin is still drawn.
        Assert.Equal(500, route.Next?.Position.X);

        route.ApplyFix(At(10, 0));

        Assert.Single(route.Waypoints);
        Assert.DoesNotContain(route.Waypoints, w => w.Visited);
    }

    [Fact]
    public void RemoveOnArrivalDropsTheMarkerStraightAway()
    {
        var route = Route((0, 0), (500, 0));
        route.Arrival = WaypointArrival.RemoveOnArrival;

        Assert.True(route.ApplyFix(At(10, 0)));

        Assert.Single(route.Waypoints);
        Assert.Equal(500, route.Waypoints[0].Position.X);
    }

    [Fact]
    public void AMarkerOutsideTheRadiusIsLeftAlone()
    {
        var route = Route((0, 0));

        Assert.False(route.ApplyFix(At(51, 0)));
        Assert.False(route.Waypoints[0].Visited);
    }

    [Fact]
    public void TheRadiusIsMeasuredOnTheGroundAndIgnoresHeight()
    {
        // Waypoints are placed by clicking a flat map, so they have no meaningful height. Judging
        // arrival in three dimensions would make a marker under a catwalk unreachable.
        var route = Route((0, 0));

        Assert.True(route.ApplyFix(new GamePosition(10, 400, 0)));
    }

    [Fact]
    public void PassingAnyMarkerCountsAsReachingIt()
    {
        // Walking past number two on the way to number one still means you were there.
        var route = Route((0, 0), (1000, 0));
        route.Arrival = WaypointArrival.RemoveOnArrival;

        route.ApplyFix(At(1000, 10));

        Assert.Single(route.Waypoints);
        Assert.Equal(0, route.Waypoints[0].Position.X);
    }

    [Fact]
    public void RemainingMarkersAreRenumberedAfterOneIsReached()
    {
        var route = Route((0, 0), (1000, 0), (2000, 0));
        route.Arrival = WaypointArrival.RemoveOnArrival;

        route.ApplyFix(At(0, 0));

        Assert.Equal([1, 2], route.Waypoints.Select(w => w.Number));
    }

    [Fact]
    public void AnEmptyRouteIsNotAChange()
    {
        Assert.False(new WaypointOverlay().ApplyFix(At(0, 0)));
    }

    [Fact]
    public void ClearingRemovesEverything()
    {
        var route = Route((0, 0), (1, 1));
        route.Clear();

        Assert.Empty(route.Waypoints);
        Assert.Null(route.Next);
    }

    [Fact]
    public void RemoveLastUndoesTheMostRecentMarker()
    {
        var route = Route((0, 0), (1000, 0));

        Assert.True(route.RemoveLast());
        Assert.Equal(0, Assert.Single(route.Waypoints).Position.X);

        Assert.True(route.RemoveLast());
        Assert.False(route.RemoveLast());
    }

    [Fact]
    public void WaypointsHandsOutACopySoACallerCannotSeeItMutate()
    {
        var route = Route((0, 0));

        var snapshot = route.Waypoints;
        route.Add(At(500, 0), new MapPoint(500, 0));

        Assert.Single(snapshot);
        Assert.Equal(2, route.Waypoints.Count);
    }

    [Fact]
    public void ArrivingWhileTheRouteIsBeingDrawnDoesNotThrow()
    {
        // The folder-watcher thread retires markers while the UI thread is still placing them, and
        // the render thread walks the list throughout.
        var route = new WaypointOverlay { ArrivalRadiusMeters = 50 };
        var failures = new List<Exception>();
        var stop = false;

        var writer = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < 3000; i++)
                {
                    route.Add(At(i * 10, 0), new MapPoint(i, 0));
                    route.ApplyFix(At(i * 10, 0));
                }
            }
            catch (Exception ex)
            {
                lock (failures) failures.Add(ex);
            }
            finally
            {
                Volatile.Write(ref stop, true);
            }
        });

        var reader = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    foreach (var waypoint in route.Waypoints)
                        _ = waypoint.Base.X;

                    _ = route.Next;
                }
            }
            catch (Exception ex)
            {
                lock (failures) failures.Add(ex);
            }
        });

        writer.Start();
        reader.Start();

        Assert.True(writer.Join(TimeSpan.FromSeconds(30)), "writer did not finish");
        reader.Join(TimeSpan.FromSeconds(5));

        Assert.True(failures.Count == 0, string.Join("; ", failures.Select(f => f.Message)));
    }
}

/// <summary>
/// Which target the guide line points at when both a route and an exit are set.
/// </summary>
public sealed class GuideTargetTests
{
    private static MapPoi Exit(double x, double z) => new()
    {
        Kind = PoiKind.ExtractPmc,
        Name = "Some Exit",
        Position = new GamePosition(x, 0, z),
        Base = new MapPoint(x, z),
    };

    [Fact]
    public void TheChosenExitIsUsedWhenThereIsNoRoute()
    {
        var line = new ExtractLineOverlay
        {
            Target = Exit(300, 0),
            PlayerPosition = new GamePosition(0, 0, 0),
        };

        Assert.Equal(300, line.DistanceMeters);
        Assert.Equal(new MapPoint(300, 0), line.GuideBase);
    }

    [Fact]
    public void AMarkerTakesPrecedenceOverTheChosenExit()
    {
        // The route is a statement about where to go next; the exit is where to end up.
        var line = new ExtractLineOverlay
        {
            Target = Exit(300, 0),
            Waypoint = new Waypoint { Position = new GamePosition(50, 0, 0), Base = new MapPoint(50, 0) },
            PlayerPosition = new GamePosition(0, 0, 0),
        };

        Assert.Equal(50, line.DistanceMeters);
        Assert.Equal(new MapPoint(50, 0), line.GuideBase);
    }

    [Fact]
    public void ClearingTheRouteHandsTheLineBackToTheExit()
    {
        var line = new ExtractLineOverlay
        {
            Target = Exit(300, 0),
            Waypoint = new Waypoint { Position = new GamePosition(50, 0, 0), Base = new MapPoint(50, 0) },
            PlayerPosition = new GamePosition(0, 0, 0),
        };

        line.Waypoint = null;

        Assert.Equal(300, line.DistanceMeters);
    }

    [Fact]
    public void NothingToPointAtMeansNoReading()
    {
        var line = new ExtractLineOverlay { PlayerPosition = new GamePosition(0, 0, 0) };

        Assert.Null(line.DistanceMeters);
        Assert.Null(line.GuideBase);
        Assert.Null(line.RelativeBearingDegrees);
    }
}
