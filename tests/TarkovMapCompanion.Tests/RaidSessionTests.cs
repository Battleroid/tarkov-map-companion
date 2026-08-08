using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Screenshots;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Raid boundaries are inferred from the two clocks in a screenshot name, so these tests are
/// anchored on real captures rather than synthetic ones wherever possible.
/// </summary>
public sealed class RaidSessionTests
{
    private static PlayerFix Fix(string time, double raidHours, double x = 0, double z = 0)
    {
        var parts = time.Split(':');
        var takenAt = new DateTime(2026, 8, 7, int.Parse(parts[0]), int.Parse(parts[1]), 0);

        return new PlayerFix
        {
            Position = new GamePosition(x, -50, z),
            YawDegrees = 0,
            Rotation = (0, 0, 0, 1),
            RaidTimeHours = raidHours,
            TakenAt = takenAt,
            FilePath = $@"C:\s\{time}-{raidHours}.png",
        };
    }

    // ---- Real captures ------------------------------------------------------

    [Fact]
    public void ConsecutiveShotsTwentySecondsApart_AreOneRaid()
    {
        // 19:19 -> 19:20, raid 10.33 -> 10.37. The minute-resolution filename reports 60 s.
        Assert.True(RaidSession.IsSameRaid(Fix("19:19", 10.33), Fix("19:20", 10.37)));
    }

    [Fact]
    public void ShotsNineMinutesApartWithTheClockKeepingPace_AreOneRaid()
    {
        // 19:20 -> 19:29, raid 10.37 -> 11.43: 3816 game-seconds over 540 real, almost exactly 7x.
        Assert.True(RaidSession.IsSameRaid(Fix("19:20", 10.37), Fix("19:29", 11.43)));
    }

    [Fact]
    public void AClockThatRunsBackwards_IsADifferentRaid()
    {
        // 18:17 -> 19:19, raid 15.07 -> 10.33. Unmistakably a new raid.
        Assert.False(RaidSession.IsSameRaid(Fix("18:17", 15.07), Fix("19:19", 10.33)));
    }

    [Fact]
    public void TheWholeRealSequenceSplitsWhereTheClocksSayItShould()
    {
        // Every screenshot in Casey's folder, in capture order.
        PlayerFix[] fixes =
        [
            Fix("17:12", 7.47), Fix("17:13", 7.47), Fix("17:13", 7.47),
            Fix("17:50", 11.88), Fix("17:52", 12.07), Fix("17:52", 12.12), Fix("17:52", 12.17),
            Fix("17:54", 12.33), Fix("17:59", 12.94), Fix("18:02", 13.30),
            Fix("18:14", 14.66), Fix("18:15", 14.76), Fix("18:17", 15.07),
            Fix("19:19", 10.33), Fix("19:20", 10.37), Fix("19:29", 11.43), Fix("19:31", 11.67),
        ];

        var raids = RaidSession.Split(fixes);

        // Two, not three. The 18:17 -> 19:19 boundary is obvious because the clock runs backwards.
        //
        // The 17:13 -> 17:50 boundary is NOT detectable from the clocks: 37 real minutes against
        // 4.41 game-hours is 7.15x, right on the money, and 37 minutes is a perfectly ordinary
        // raid length. Those really are two raids -- the height jumps from +5 to -53 because they
        // are different maps -- but that is a map-change signal, not a clock signal, and it is
        // handled by MapSession clearing the trail when the map changes.
        Assert.Equal(2, raids.Count);
        Assert.Equal(13, raids[0].Count);
        Assert.Equal(4, raids[1].Count);
    }

    [Fact]
    public void ShotsInTheSameMinute_AreNotSplitByTheMinuteResolutionOfTheFilename()
    {
        // The 17:52 trio reports a zero-second gap while the raid clock advances; the slack in
        // the tolerance is what stops this being read as three separate raids.
        Assert.True(RaidSession.IsSameRaid(Fix("17:52", 12.07), Fix("17:52", 12.12)));
        Assert.True(RaidSession.IsSameRaid(Fix("17:52", 12.12), Fix("17:52", 12.17)));
        Assert.True(RaidSession.IsSameRaid(Fix("17:52", 12.17), Fix("17:54", 12.33)));
    }

    // ---- Boundary rules -----------------------------------------------------

    [Fact]
    public void AGapLongerThanAnyRaid_IsADifferentRaidEvenIfTheClockLinesUp()
    {
        var previous = Fix("12:00", 8.00);
        var next = Fix("14:00", 8.00 + 2 * RaidSession.GameClockRate);

        Assert.False(RaidSession.IsSameRaid(previous, next));
    }

    [Fact]
    public void AClockAdvancingFarFasterThanSevenTimes_IsADifferentRaid()
    {
        // Ten real minutes should buy about 70 game-minutes; five game-hours cannot be the same raid.
        Assert.False(RaidSession.IsSameRaid(Fix("12:00", 8.0), Fix("12:10", 13.0)));
    }

    [Fact]
    public void AClockBarelyMovingOverALongGap_IsADifferentRaid()
    {
        // Thirty real minutes with the raid clock almost frozen: the player left and came back.
        Assert.False(RaidSession.IsSameRaid(Fix("12:00", 8.0), Fix("12:30", 8.05)));
    }

    [Fact]
    public void AFixThatArrivesOutOfOrder_StartsANewRaidRatherThanCorruptingTheTrail()
    {
        Assert.False(RaidSession.IsSameRaid(Fix("12:10", 9.0), Fix("12:00", 8.0)));
    }

    [Fact]
    public void MaxRaidLengthIsHonored()
    {
        var previous = Fix("12:00", 8.0);
        var next = Fix("12:25", 8.0 + 25.0 / 60.0 * RaidSession.GameClockRate);

        Assert.True(RaidSession.IsSameRaid(previous, next, TimeSpan.FromMinutes(60)));
        Assert.False(RaidSession.IsSameRaid(previous, next, TimeSpan.FromMinutes(20)));
    }

    // ---- Ordering -----------------------------------------------------------

    [Fact]
    public void InChronologicalOrder_BreaksSameMinuteTiesOnTheRaidClock()
    {
        // Sorting on the filename timestamp alone leaves these three in whatever order the file
        // system returned, which for the real captures is exactly backwards.
        PlayerFix[] scrambled = [Fix("17:52", 12.17), Fix("17:52", 12.07), Fix("17:52", 12.12)];

        var ordered = RaidSession.InChronologicalOrder(scrambled);

        Assert.Equal([12.07, 12.12, 12.17], ordered.Select(f => f.RaidTimeHours));
    }

    [Fact]
    public void Split_OrdersInputItself()
    {
        PlayerFix[] scrambled = [Fix("19:31", 11.67), Fix("19:19", 10.33), Fix("19:29", 11.43)];

        var raids = RaidSession.Split(scrambled);

        Assert.Single(raids);
        Assert.Equal([10.33, 11.43, 11.67], raids[0].Select(f => f.RaidTimeHours));
    }

    // ---- Elapsed ------------------------------------------------------------

    [Fact]
    public void ElapsedIn_ConvertsGameTimeBackToRealMinutes()
    {
        // 10.33 -> 11.67 game-hours is 1.34 h of game time, which is 11.5 real minutes at 7x.
        IReadOnlyList<PlayerFix> raid = [Fix("19:19", 10.33), Fix("19:31", 11.67)];

        var elapsed = RaidSession.ElapsedIn(raid, raid[1]);

        Assert.Equal(11.49, elapsed.TotalMinutes, 1);
    }

    [Fact]
    public void ElapsedIn_IsZeroAtTheStartOfARaid()
    {
        IReadOnlyList<PlayerFix> raid = [Fix("19:19", 10.33)];

        Assert.Equal(TimeSpan.Zero, RaidSession.ElapsedIn(raid, raid[0]));
    }

    // ---- Overlay integration ------------------------------------------------

    [Fact]
    public void PlayerOverlay_DropsThePreviousTrailWhenANewRaidStarts()
    {
        var overlay = new TarkovMapCompanion.Rendering.PlayerOverlay();
        var raidStarts = 0;
        overlay.RaidStarted += (_, _) => raidStarts++;

        overlay.Add(Fix("19:19", 10.33));
        overlay.Add(Fix("19:20", 10.37));
        overlay.Add(Fix("19:29", 11.43));
        Assert.Equal(3, overlay.History.Count);
        Assert.Equal(0, raidStarts);

        // A fix from an entirely different raid.
        overlay.Add(Fix("21:05", 4.20));

        Assert.Single(overlay.History);
        Assert.Equal(1, raidStarts);
        Assert.Equal(4.20, overlay.Current!.RaidTimeHours);
    }

    [Fact]
    public void PlayerOverlay_ExpiresTrailPointsOlderThanARaidCanRun()
    {
        // The backstop for boundaries the clock heuristic cannot see. With a 45 minute ceiling,
        // anything more than 45 real minutes behind the newest fix drops off the trail.
        var overlay = new TarkovMapCompanion.Rendering.PlayerOverlay
        {
            MaxRaidLength = TimeSpan.FromMinutes(45),
        };

        overlay.Add(Fix("17:12", 7.47));
        overlay.Add(Fix("17:50", 11.88));   // 37 real minutes later: still inside the window
        Assert.Equal(2, overlay.History.Count);

        overlay.Add(Fix("18:02", 13.30));   // now 48 minutes past the first fix
        Assert.DoesNotContain(overlay.History, f => f.RaidTimeHours == 7.47);
        Assert.Equal(2, overlay.History.Count);
    }

    [Fact]
    public void PlayerOverlay_ReportsElapsedTimeForTheCurrentRaidOnly()
    {
        var overlay = new TarkovMapCompanion.Rendering.PlayerOverlay();

        overlay.Add(Fix("19:19", 10.33));
        overlay.Add(Fix("19:31", 11.67));
        Assert.Equal(11.49, overlay.RaidElapsed.TotalMinutes, 1);

        overlay.Add(Fix("21:05", 4.20));
        Assert.Equal(TimeSpan.Zero, overlay.RaidElapsed);
    }
}
